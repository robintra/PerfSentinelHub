# Exploitation

## Fraîcheur et rétablissement

Le push est primaire. Chaque source est aussi pollée indépendamment, en filet de sécurité.

Un poll réussi met à jour les observations. Il ne supprime pas un finding au seul motif
qu'une réponse ultérieure du daemon l'omet : le tampon circulaire du daemon peut l'avoir
évincé, donc absent ne veut **pas** dire résolu. Seule la rétention retire des findings,
quand leur dernière observation est plus ancienne que la période configurée.

Le daemon perf-sentinel 0.11.x plafonne `/api/findings` à 1 000 lignes. Le Hub utilise ce
plafond exact et avertit chaque fois qu'il est atteint, l'instantané du filet pouvant être
incomplet. Une couverture à fort volume exige donc l'exportateur de push borné.

Les échecs laissent lisibles les findings déjà stockés. La source concernée est marquée
injoignable et réessayée avec un repli exponentiel borné, et un succès ultérieur efface
cet état. Les corps de poll sont limités à 16 Mio, les requêtes ont un timeout, les
imports sont transactionnels, et les logs n'identifient que l'identifiant de source et un
code d'erreur stable.

## Métriques

`GET /metrics` sert le format texte Prometheus. Il est écrit à la main plutôt
qu'au travers d'une bibliothèque : huit familles de métriques sur des données que
le Hub détient déjà ne justifient pas une dépendance dans un service dont les
deux seuls paquets sont SQLite.

| Métrique                                        | Type  | Ce à quoi elle répond                                     |
|-------------------------------------------------|-------|-----------------------------------------------------------|
| `perf_sentinel_hub_build_info{version}`         | gauge | Quelle version tourne                                     |
| `perf_sentinel_hub_source_reachable{source}`    | gauge | Si le dernier poll d'un daemon a réussi                   |
| `perf_sentinel_hub_source_unreachable_seconds`  | gauge | Depuis combien de temps il est injoignable, 0 s'il répond |
| `perf_sentinel_hub_source_last_success_seconds` | gauge | L'âge du dernier poll réussi                              |
| `perf_sentinel_hub_analysis_queue_depth`        | gauge | Les runs acceptés et pas encore pris par un worker        |
| `perf_sentinel_hub_source_last_import_seconds`  | gauge | Dernier push d'un daemon. Pas un battement de cœur, voir plus bas |
| `perf_sentinel_hub_import_rejected_total{reason}` | counter | Imports refusés, par motif                            |
| `perf_sentinel_hub_analysis_runs{status}`       | gauge | Les runs actuellement stockés, par statut                 |

Trois partis pris dans cette forme.

Seul un daemon reçoit une série de source, et seulement un que le Hub a
réellement observé. Un backend de traces n'est jamais interrogé, et un daemon
sans ligne `source_state` n'a jamais été joint du tout, donc publier une valeur
pour l'un ou l'autre affirmerait une chose que le Hub n'a pas vue. Cette ligne
est aussi ce que la rétention supprime pour une source qu'elle a cessé
d'interroger, donc une source oubliée depuis longtemps devient silencieuse
plutôt que de passer au vert.

Un daemon jamais interrogé avec succès ne reçoit aucune série `last_success`.
Zéro se lirait comme "a réussi à l'instant", l'inverse de jamais.

`analysis_runs` est une gauge, pas un compteur `_total`. Un run passe d'un statut
à l'autre, et un run terminé expire sur `Hub:Analysis:RunRetention`, donc chaque
série descend autant qu'elle monte. Un run encore `pending` ou `running` n'est
jamais purgé quel que soit l'âge apparent de sa ligne, un worker s'apprêtant à y
écrire ou y écrivant déjà. Chaque statut est émis même à zéro, une gauge qui
disparaît se lisant comme un échec de collecte plutôt que comme "rien n'est dans
cet état".

La cardinalité est bornée par la configuration. `source` prend les identifiants
de `Hub:Sources`, fixés au démarrage et restreints à 1 à 64 caractères ASCII
alphanumériques, `.`, `_` ou `-`. `status` prend six constantes. Rien de ce
qu'envoie un appelant n'atteint un libellé.

L'endpoint ne porte aucune authentification, exactement comme `/api/status`.
Gardez-le derrière ce qui protège déjà le reste du Hub. Il partage cette
origine avec le lanceur, donc un proxy ouvert pour le navigateur sert aussi
`/metrics` tant que le chemin n'est pas exclu. Le chart laisse la
collecte en opt-in plutôt que de la supposer :

```yaml
service:
  annotations:
    prometheus.io/scrape: "true"
    prometheus.io/port: "8080"
    prometheus.io/path: /metrics
```

### Les consommer

Trois fichiers sous [`examples/`](../../examples), validés plutôt qu'esquissés.

| Fichier                                                           | Est                                                         |
|-------------------------------------------------------------------|-------------------------------------------------------------|
| [`grafana-dashboard.json`](../../examples/grafana-dashboard.json) | Neuf panneaux sur les huit familles, importable tel quel    |
| [`prometheus-alerts.yml`](../../examples/prometheus-alerts.yml)   | Une règle, contrôlée par `promtool check rules`             |
| [`prometheus-scrape.yml`](../../examples/prometheus-scrape.yml)   | Un job de collecte pour un déploiement qui nomme ses cibles |

Le moteur livre son propre tableau de bord pour ses propres métriques, et les
deux ne se recouvrent pas : aucun panneau d'ici ne lit une série de daemon, et
aucun panneau de là-bas ne lit une série du Hub. Importer les deux pour
surveiller une flotte et le Hub qui la collecte.

### Pourquoi une seule alerte

Le Hub n'est sur le chemin de requête d'aucune production, et le push est le
chemin primaire : un daemon poste ses findings, les retient et les rejoue par
lots coalescés. Donc presque rien de ce que le Hub peut rapporter ne mérite de
réveiller quelqu'un, et une alerte qui se déclenche sur une condition qu'un
lecteur aurait vue sur un panneau n'est que du bruit. Quatre règles ont été
écrites puis retirées :

| Retirée                     | Pourquoi, et où la condition se voit à la place                                                                                                                                    |
|-----------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Une source est injoignable  | Elle surveille le filet de poll, pas le chemin de push, donc elle passe au rouge sur une flotte dont tous les daemons livrent. L'écran de flotte et deux panneaux la dessinent déjà |
| Une source est périmée      | Même angle mort, et quand le jour est écoulé le panneau de dernière réussite dessine la montée depuis un jour                                                                       |
| La file d'analyse s'accumule | Du travail soumis par des humains : une profondeur de 20, ce sont 20 personnes qui ont cliqué. `GET /api/status` le montre à celui-là même qui a soumis, et rien ne se perd à attendre |
| Des runs ont été interrompus | Se déclenche sur l'événement le plus banal qui soit, un redémarrage qui a attrapé un run en file, et ne se tait jamais puisque rien ne supprime ces lignes                          |

Ce qui survit est la seule condition qu'aucun tableau de bord ne peut montrer,
puisqu'un Hub mort ne publie aucune série et que tous les panneaux se vident
exactement comme sur une collecte mal configurée.

Le compteur d'imports est nouveau et ne mérite toujours pas de règle, ce qui
vaut d'être dit puisqu'on s'attendrait au contraire. `unauthorized` monte quand
une clé expire, et aussi quand n'importe qui poste un `source_id` inconnu, et le
Hub ne peut pas distinguer les deux : le seul label qui les séparerait est le
`source_id` de l'appelant, c'est-à-dire précisément la valeur non bornée qui ne
doit jamais atteindre un label. Alerter dessus reviendrait à donner à un inconnu
le moyen de vous réveiller, et `bad_request` aussi, dont la moitié query string
est contrôlée avant la clé. `gate_full` et `write_timeout` sont une
contre-pression que le daemon retraverse en rejouant, séparés parce qu'ils
nomment deux boutons différents : trop de téléverseurs à la fois, contre un
écrivain retenu par le poll ou par la rétention. La moitié de `bad_request` qui
exige une clé, comme `too_large`, se corrige dans le dépôt de l'exporteur
plutôt qu'ici. Les quatre relèvent du panneau, où un humain les
lit avec le contexte qui les distingue.

Deux manques subsistent, à nommer plutôt qu'à maquiller. Un push bloqué avant
d'arriver, par une politique réseau par exemple, ne produit aucune requête donc
aucun rejet : le compteur ne voit pas la panne la plus courante, et
`source_last_import_seconds` non plus, un daemon sans nouveau finding ne poussant
rien et ressemblant exactement à cela. Les distinguer demanderait une provenance
par finding que le Hub ne stocke pas. Et Prometheus ne détient aucune liste des
sources qui devraient exister, donc un daemon que le Hub n'a jamais joint est
silencieux plutôt qu'alarmant, ce qui relève de l'écran de flotte.

## Sauvegarde

L'historique `first_seen` est la seule chose que le Hub stocke qu'aucun amont ne peut
reconstruire. Le tampon circulaire du daemon oublie, donc perdre le volume perd la
chronologie.

Le binaire livre une commande `backup` qui prend un instantané de la base vivante par le
`VACUUM INTO` de SQLite, sûr à côté de l'unique écrivain grâce au WAL. Elle lit
`Hub:DatabasePath` depuis la même configuration que le serveur, refuse d'écraser une
destination existante, et retire son fichier partiel quand la copie échoue.

L'instantané est une copie complète écrite sur le même volume, donc gardez au moins la
taille de la base elle-même de libre sur le PVC avant d'en démarrer un. Le défaut du chart
est de 1 Gio au total.

```bash
kubectl exec deploy/perf-sentinel-hub -- /app/PerfSentinelHub backup /data/hub-backup-20260826.db
```

Datez le nom de fichier. Le garde-fou contre l'écrasement fait échouer un chemin fixe au
second passage, et l'image d'exécution chiselée n'a pas de shell pour supprimer un
reliquat.

### Sortir le fichier du cluster

`kubectl cp` ne peut pas tirer depuis le pod du Hub, qui n'a pas de tar. Montez le même
PVC dans un pod éphémère, copiez depuis là, et retirez l'instantané de ce pod.

Le volume est `ReadWriteOnce`, donc épinglez l'assistant au nœud où tourne le pod du Hub.
Faites-le tourner sous l'uid du Hub pour qu'il puisse lire et supprimer les fichiers, et
donnez-lui le contexte de sécurité qu'exige un namespace `restricted` :

```bash
NODE=$(kubectl get pod -l app.kubernetes.io/name=perf-sentinel-hub -o jsonpath='{.items[0].spec.nodeName}')
kubectl apply -f - <<EOF
apiVersion: v1
kind: Pod
metadata:
  name: hub-backup-fetch
spec:
  nodeName: ${NODE}
  securityContext:
    runAsNonRoot: true
    runAsUser: 1654
    runAsGroup: 1654
    seccompProfile: { type: RuntimeDefault }
  containers:
    - name: fetch
      image: busybox:1.37
      command: ["sleep", "3600"]
      securityContext:
        allowPrivilegeEscalation: false
        capabilities: { drop: ["ALL"] }
      volumeMounts: [{ name: data, mountPath: /data }]
  volumes:
    - name: data
      persistentVolumeClaim: { claimName: perf-sentinel-hub }
EOF
kubectl cp hub-backup-fetch:/data/hub-backup-20260826.db ./hub-backup.db
kubectl exec hub-backup-fetch -- rm /data/hub-backup-20260826.db
kubectl delete pod hub-backup-fetch
```

En local, `make backup DB=/chemin/vers/hub.db DEST=backup.db` enveloppe la même commande.

## Restauration

Remplacez le fichier de base pendant que le Hub est complètement arrêté. Ramenez le
Deployment à zéro et attendez que le pod se termine réellement avant de toucher au volume,
un arrêt gracieux pouvant prendre des dizaines de secondes :

```bash
kubectl scale deploy/perf-sentinel-hub --replicas=0
kubectl wait --for=delete pod -l app.kubernetes.io/name=perf-sentinel-hub --timeout=120s
```

Ensuite, depuis le pod assistant, copiez la sauvegarde par-dessus `/data/hub.db`,
supprimez les `hub.db-wal` et `hub.db-shm` périmés à côté, et remontez l'échelle.
