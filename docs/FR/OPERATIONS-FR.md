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
