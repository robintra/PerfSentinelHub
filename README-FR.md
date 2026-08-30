<p align="center">
    <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fraw.githubusercontent.com%2Frobintra%2FPerfSentinelHub%2Fmain%2Fglobal.json&query=%24.sdk.version&label=.NET&color=512BD4&logo=dotnet&logoColor=white" alt=".NET" /></a>
    <a href="https://github.com/robintra/PerfSentinelHub/actions/workflows/ci.yml"><img src="https://github.com/robintra/PerfSentinelHub/actions/workflows/ci.yml/badge.svg" alt="CI" /></a>
    <a href="https://github.com/robintra/PerfSentinelHub/actions/workflows/security-audit.yml"><img src="https://github.com/robintra/PerfSentinelHub/actions/workflows/security-audit.yml/badge.svg" alt="Security Audit" /></a>
    <a href="https://github.com/robintra/PerfSentinelHub/actions/workflows/codeql.yml"><img src="https://github.com/robintra/PerfSentinelHub/actions/workflows/codeql.yml/badge.svg" alt="CodeQL" /></a>
    <a href="https://sonarcloud.io/summary/overall?id=robintrassard_PerfSentinelHub"><img src="https://sonarcloud.io/api/project_badges/measure?project=robintrassard_PerfSentinelHub&metric=coverage" alt="Coverage" /></a>
    <a href="https://sonarcloud.io/summary/overall?id=robintrassard_PerfSentinelHub"><img src="https://sonarcloud.io/api/project_badges/measure?project=robintrassard_PerfSentinelHub&metric=alert_status" alt="Quality Gate" /></a>
    <a href="https://github.com/robintra/PerfSentinelHub/actions/workflows/release.yml"><img src="https://github.com/robintra/PerfSentinelHub/actions/workflows/release.yml/badge.svg" alt="Release" /></a>
</p>

# PerfSentinelHub

PerfSentinelHub donne aux greffons d'IDE un point d'accès durable et unique pour les findings
collectés depuis une ou plusieurs instances de
[perf-sentinel](https://github.com/robintra/perf-sentinel).
C'est un service NativeAOT adossé à SQLite : le push depuis le daemon est le chemin primaire, le
poll horaire est un filet de sécurité, et le Hub conserve par défaut pendant 180 jours des
enveloppes de findings compatibles en lecture.

Chaque badge ci-dessus rapporte quelque chose d'observé, sauf celui de la release, qui reste vide
tant que le workflow de publication n'a pas tourné une première fois. Les badges de l'image de
conteneur et du chart Helm sont délibérément absents : leurs pages de paquet répondent 404 tant
qu'une release ne les a pas publiés, et un badge qui ne mène nulle part est une promesse plutôt
qu'une preuve. Ils reviendront avec la première release.

## Contrat de release et maturité

Le contrat de release configuré n'accepte que des tags stables `v0.x.y`. "Stable" veut dire qu'une
release ne porte ni suffixe de préversion ni canal bêta. `0.x.y` marque toujours une maturité
antérieure à la 1.0, donc la compatibilité peut changer d'une version mineure à l'autre. La
première release configurée est `v0.1.0`, et la publication n'est pas affirmée tant que la
destination de release liée et le workflow de vérification publique ne la montrent pas.

Chaque release est close sur ces quatre cibles d'exécution NativeAOT et leurs archives de symboles
correspondantes :

- `linux-x64`
- `linux-arm64`
- `osx-arm64`
- `win-x64`

Il n'y a pas d'artefact macOS AMD64 ni Windows ARM64. La même release close contient aussi une
archive d'image OCI Linux multi-architecture, un chart Helm lié par digest, un document SPDX et un
bundle Cosign pour chaque sujet, plus la provenance GitHub. `release-manifest.json` et `SHA256SUMS`
lient les noms de fichiers exacts, le commit source et les digests.

## Démarrer en local en cinq minutes

Prérequis : le SDK .NET 10.0.400 et un daemon perf-sentinel joignable.

```bash
Hub__DatabasePath=/tmp/perf-sentinel-hub.db \
Hub__Sources__0__Id=local \
Hub__Sources__0__Name='Local daemon' \
Hub__Sources__0__Environment=development \
Hub__Sources__0__BaseUrl=http://localhost:4318 \
Hub__Sources__0__ImportApiKey="$(openssl rand -hex 16)" \
ASPNETCORE_URLS=http://localhost:5080 \
dotnet run --project PerfSentinelHub

curl http://localhost:5080/health/ready
curl http://localhost:5080/api/findings
```

Le premier poll démarre immédiatement. Le fichier SQLite survit aux redémarrages au chemin
configuré.

## Installer avec Helm depuis les sources

Pour une évaluation locale, le chart source déploie toujours un réplica et un volume persistant.
Fournissez au moins une source et un digest d'image immuable :

```bash
helm upgrade --install perf-sentinel-hub deploy/helm/perf-sentinel-hub \
  --set image.repository=ghcr.io/robintra/perf-sentinel-hub \
  --set image.digest=sha256:IMAGE_DIGEST \
  --set 'sources[0].id=production' \
  --set 'sources[0].name=Production' \
  --set 'sources[0].environment=production' \
  --set 'sources[0].baseUrl=http://perf-sentinel.observability:4318' \
  --set persistence.size=5Gi
```

Pour un poll de daemon authentifié, mettez la valeur dans un Secret Kubernetes, puis réglez
`sources[].authHeaderName`, `sources[].authSecretName` et `sources[].authSecretKey`. Ne mettez
jamais l'identifiant lui-même dans les values Helm. Pour le push depuis le daemon, réglez
`sources[].importSecretName` et `sources[].importSecretKey`, la valeur référencée devant contenir
au moins 32 caractères.

Pour une release publique, utilisez le digest d'image enregistré dans son manifeste de release
authentifié. Résolvez une fois le digest de registre du chart, notez-le, et ne tirez ou n'installez
que `oci://...@sha256:...`. Un tag de version est un indice de découverte, jamais une identité de
déploiement :

```bash
IMAGE_DIGEST="$(jq -r .image.digest release/release-manifest.json)"
docker pull "ghcr.io/robintra/perf-sentinel-hub@$IMAGE_DIGEST"

CHART=ghcr.io/robintra/charts/perf-sentinel-hub
CHART_DIGEST="$(oras resolve "$CHART:0.1.0")"
helm pull "oci://$CHART@$CHART_DIGEST"
```

Ces commandes de registre ne sont utilisables qu'une fois la répétition publique et la publication
réussies.

## Configuration

Les variables d'environnement suivent la forme .NET `Hub__...`, et Helm expose les mêmes réglages
sous `hub` et `sources`.

| Réglage | Défaut | Validation |
| --- | --- | --- |
| `Hub:DatabasePath` | `/data/hub.db` | Chemin absolu |
| `Hub:PollInterval` | `01:00:00` | Durée positive |
| `Hub:HttpTimeout` | `00:00:10` | Durée positive |
| `Hub:MaxConcurrentPolls` | `4` | 1 à 32 |
| `Hub:Retention` | `180.00:00:00` (180 jours) | Durée positive |
| `Hub:ResolutionGrace` | `7.00:00:00` (7 jours) | Positive, inférieure à `Retention` |
| `Hub:DefaultReadLimit` | `1000` | 1 à `MaxReadLimit` |
| `Hub:MaxReadLimit` | `10000` | 1 à 10000 |
| `Hub:Analysis:EngineBinaryPath` | aucun | Optionnel, chemin absolu vers le binaire perf-sentinel. Absent, les runs d'analyse sont indisponibles |
| `Hub:Analysis:ReportDirectory` | `/data/reports` | Absolu, accessible en écriture. Les rapports rendus vivent ici |
| `Hub:Analysis:IdentityHeader` | `X-Forwarded-User` | En-tête qu'un reverse proxy renseigne avec l'identité du demandeur |
| `Hub:Analysis:Workers` | `2` | 1 à 16 |
| `Hub:Analysis:MaxTracesCap` | `2000` | 1 à 10000, la limite propre du moteur sur `--max-traces` |
| `Hub:Analysis:Timeout` | `00:05:00` | Positive, une heure au plus |
| `Hub:Analysis:ReportRetention` | `1.00:00:00` (24 heures) | Durée positive |
| `Hub:UpdateCheck:Enabled` | `true` | Si le Hub demande à GitHub la release publiée la plus récente de chaque produit |
| `Hub:UpdateCheck:Interval` | `1.00:00:00` (1 jour) | Au moins 15 minutes |
| `Hub:UpdateCheck:EngineEndpoint` | API des releases GitHub de `robintra/perf-sentinel` | HTTPS absolue, sans identifiants, ni query, ni fragment |
| `Hub:UpdateCheck:HubEndpoint` | API des releases GitHub de `robintra/PerfSentinelHub` | HTTPS absolue, sans identifiants, ni query, ni fragment |
| `Hub:Sources` | aucune | Au moins une source |
| `Sources[].Id` | aucun | Non vide et unique |
| `Sources[].Name` | aucun | Non vide |
| `Sources[].Environment` | aucun | Non vide |
| `Sources[].Kind` | `daemon` | L'un de `daemon`, `tempo`, `jaeger_query`. Seul un daemon est pollé et seul un daemon peut porter une clé d'import |
| `Sources[].RetentionHours` | aucun | Backends de traces seulement, d'une heure à dix ans. Jusqu'où ce backend conserve les traces, déclaré parce qu'aucune API de backend ne l'expose |
| `Sources[].BaseUrl` | aucune | Obligatoire, URL HTTP(S) absolue sans identifiants, query ni fragment. Un préfixe de chemin est conservé, donc `https://gw/perf-sentinel/` polle `https://gw/perf-sentinel/api/status` |
| `Sources[].AuthHeaderName/Value` | aucun | Les deux absents ou les deux présents, sans saut de ligne |
| `Sources[].ImportApiKey` | aucune | Identifiant de push optionnel, au moins 32 caractères, fourni via un Secret |

### Où le Hub se connecte

Chaque requête sortante va vers un `Sources[].BaseUrl` configuré, avec une exception : la
vérification de mise à jour, qui demande à l'API des releases GitHub quelle est la version publiée
la plus récente du moteur et du Hub, une fois par jour. Elle existe pour que la pastille de version
puisse dire que ce que vous faites tourner n'est plus le plus récent, et elle ne porte aucun
identifiant de votre déploiement, seulement un GET non authentifié.

Réglez `Hub:UpdateCheck:Enabled` à `false` pour un déploiement sans sortie réseau. Éteinte, le Hub
ne rapporte de version plus récente pour rien, et la pastille n'affiche rien plutôt que d'affirmer
que vous êtes à jour, ce qui est exactement ce qu'elle affiche quand la requête échoue.

### Une source https avec une CA privée

Le Hub valide le certificat d'une source contre le magasin de confiance du conteneur, donc un
daemon en cluster portant un certificat auto-signé ou émis en interne est refusé avec
`PartialChain` tant que sa CA n'y est pas. L'image d'exécution est chiselée et n'a pas de shell,
donc `update-ca-certificates` n'y est pas exécutable. Pointez plutôt `SSL_CERT_FILE` vers un
bundle, monté depuis une ConfigMap :

```yaml
env:
  - name: SSL_CERT_FILE
    value: /etc/perf-sentinel-hub/certs/bundle.crt
```

Le bundle doit être les racines publiques et votre CA concaténées, dans cet ordre, et non la CA
seule : la variable remplace le fichier par défaut au lieu de s'y ajouter, et un Hub qui ne fait
confiance qu'à votre CA ne peut rien joindre sur l'internet public. Vérifié contre l'exécution sur
laquelle cette image est bâtie : sans la variable le certificat privé est refusé et le TLS public
fonctionne, avec le bundle concaténé les deux fonctionnent.

## API d'import

`POST /api/import/findings?source_id=<id>` accepte l'enveloppe JSON du daemon
`{"producer_version":"…","findings":[…]}` avec `X-API-Key`. Une requête contient de 1 à 100
findings et 2 Mio au plus. La réponse n'est envoyée qu'une fois l'upsert idempotent par signature
commité.

Le Hub admet quatre imports à la fois, ce qui borne la mémoire des requêtes indépendamment du
nombre de daemons, et les écritures elles-mêmes sont sérialisées face aux chemins de poll et de
rétention. Un import qui ne peut pas prendre le verrou d'écriture en cinq secondes reçoit
`503 Retry-After: 1`, et les exportateurs des daemons conservent puis rejouent leurs lots
fusionnés. La rétention purge par tranches bornées pour qu'une purge longue ne rejette pas les
imports pendant toute sa durée.

Un push met à jour les findings et les observations par source, rien d'autre. Il n'efface jamais
le `unreachable_since_ms` du chemin de poll, donc une source que le Hub ne peut pas joindre
continue de rapporter `unreachable_since` alors même que son daemon pousse avec succès.

## API de lecture

- `GET /api/status` rapporte le service et la version du Hub, la version du binaire perf-sentinel
  qu'il lancerait (`engine_version`, null quand aucun n'est configuré), et ce que coûte un run :
  le nombre de workers, la profondeur de file courante, ainsi que le plafond de traces, le timeout
  et la rétention de rapport qu'il applique.
- `GET /api/sources` liste chaque source configurée avec son kind et son dernier état de collecte
  connu. Les horodatages sont null pour une source jamais observée, ce qu'un lecteur ne doit pas
  confondre avec l'epoch, et `producer_version` est null pour un backend de traces, parce qu'un
  backend stocke des traces et ne détecte rien. `retention_hours` est une valeur déclarée et non
  mesurée, et porte la même réserve que l'environnement : elle garde une affirmation périmée
  jusqu'à ce que quelqu'un l'édite.
- `GET /api/findings` accepte `service`, `finding_type`, `severity`, `limit`, `status` et le
  paramètre `include_acked` compatible avec le daemon. `include_acked` vaut `true` par défaut, et
  `include_acked=false` masque les enveloppes portant un `acknowledged_by` non null.
- `GET /api/findings/{traceId}` renvoie les findings d'une trace d'exemple.
- `GET /api/sources/{sourceId}/daemon` lit les réglages appliqués d'un daemon et le compte rendu
  qu'il fait de son propre état, à la demande plutôt que depuis le poll : les réglages ne changent
  jamais sans un redémarrage dont le Hub n'a aucun signal, et ce sont les jauges qui comptent. Il
  répond `404` pour une source inconnue et `400` pour un backend de traces, qui ne fait tourner
  aucun daemon. Un daemon qui ne répond pas est rapporté comme une observation,
  `state: "unreachable"` avec un code d'erreur, et jamais comme un `502` : le Hub relaie la santé
  d'une source, il n'échoue pas lui-même. `config` est la section `[daemon]` du daemon relayée
  verbatim, et vaut null avec `config_unavailable_reason: "api_disabled"` quand ce daemon ne sert
  aucune API de query, ce qui est une affirmation de configuration plutôt qu'une panne.
  `detection_config`, `scoring_config` et `energy_model` viennent de l'export du daemon, là où
  `/api/config` ne les porte pas. `warnings` est le conseiller de tuning du daemon lui-même,
  verbatim : le Hub relaie ces phrases et n'en écrit aucune. Un conseil au-delà de deux mille
  caractères est coupé avec une ellipse visible, tout ce qui dépasse la centaine de conseils est
  compté dans `warnings_dropped` plutôt que disparu en silence, et une lecture d'export ratée est
  nommée dans `hints_unavailable_reason` au lieu de se lire comme un bulletin de santé vierge. Une
  ligne ouverte relit à l'intervalle que choisit le lecteur, et `?refresh=status` fait de ce tic
  une simple lecture de statut plutôt que les trois que prend la vue complète : l'export, qui est
  la lourde, tourne au plus une fois par minute, et c'est lui qui porte les conseils du daemon. Une
  ligne dont la lecture a échoué relit aussi, avec cette même requête bon marché, et la première
  qui répond est suivie aussitôt d'une lecture complète, de sorte qu'une ligne laissée ouverte se
  rétablit d'elle-même au lieu d'attendre d'être repliée. Le rythme que cela impose ne peut pas
  affamer le daemon : le plafond de 32 requêtes concurrentes du moteur est cantonné à sa route
  d'ingestion OTLP précisément pour que `/api` et `/health` restent réactifs, les tics de statut ne
  prennent aucune place de lecture au Hub, et les lectures complètes sont bornées à deux à la fois,
  sur des connexions mutualisées. La surface de query du daemon est HTTP(S) seulement par
  conception, le port gRPC étant l'ingestion OTLP, donc le Hub ne lui parle aucun RPC. La seule
  chose qu'il dérive est `state`, selon qu'une jauge a franchi 90 % de son plafond, la même ligne
  que trace le monitor du daemon. Il porte aussi `daemon_defaults`, `detection_defaults` et
  `defaults_engine_version`, pour qu'un lecteur puisse marquer ce qu'un daemon a réellement changé.
  Ces défauts sont ceux du binaire que ce Hub embarque, exactement comme le `query monitor` du
  moteur compare au binaire qui le fait tourner, donc la version est nommée plutôt que supposée, et
  un daemon sur une autre mineure est signalé plutôt que jugé.

Les réponses conservent chaque finding du daemon comme un document JSON opaque et additif, et y
ajoutent des métadonnées durables `first_seen`, `last_seen`, `max_confidence`, `status`, un
`lineage` optionnel, et la fraîcheur de la source. `first_seen` vient de l'enveloppe du daemon
(`first_seen_ms`), borné à l'heure d'observation du Hub et à un plancher de bon sens en
millisecondes Unix, de sorte que ni une horloge de daemon en avance ni un bug d'unité en secondes
ne peuvent le fausser, et il retombe sur l'heure d'observation quand un producteur omet le champ.
`last_seen` est délibérément l'horloge d'observation du Hub : la rétention, l'ordonnancement et les
comparaisons de fraîcheur s'appuient dessus, donc il ne vient jamais d'une horloge distante. Les
clients d'IDE doivent ignorer les champs inconnus, comme ils le font avec l'API du daemon.
`/health/live` contrôle le process, et `/health/ready` devient positif après l'initialisation de
SQLite.

`status` est dérivé à la lecture, jamais stocké, depuis des données que le Hub garde déjà :
`active` tant que le finding a été vu dans le délai de grâce (`Hub:ResolutionGrace`, 7 jours par
défaut), `likely_resolved` quand le finding s'est tu mais que son endpoint bat encore depuis une
source joignable au-delà de la grâce, et `not_observed` quand rien ne prouve rien, endpoint
silencieux ou flotte injoignable inclus. C'est une présomption et non un verdict : un finding qui
part par rétention part toujours en silence, mais un lecteur peut désormais distinguer "l'endpoint
tourne sans le finding" de "personne ne regarde". `?status=<valeur>` filtre, et le filtre
s'applique avant la limite de page.

`first_seen` vaut par signature : un finding dont le template normalisé change reçoit une nouvelle
signature et donc un nouveau `first_seen`. Depuis le schéma v2, le Hub relie une telle mutation à
son prédécesseur à l'import, quand exactement un finding stocké partage le service, le détecteur et
l'endpoint avec un hash de template différent, a été vu dans les 30 derniers jours et strictement
avant le lot entrant, et n'est pas lui-même déjà supplanté. L'ambiguïté n'enregistre rien : nommer
l'un de plusieurs candidats serait une supposition. L'enveloppe d'un finding relié porte un objet
`lineage` avec `original_first_seen`, la naissance la plus ancienne de la chaîne, et
`predecessors`, la longueur de la chaîne. Les deux sont dénormalisés sur le maillon le plus récent
au moment du lien, de sorte que la filiation complète d'un finding survit à la purge par rétention
de chaque étape antérieure. L'heuristique est conservatrice et non destructive : les deux lignes
restent des findings distincts, et le prédécesseur vieillit par la rétention ordinaire.

## API d'analyse

Une analyse est un run du binaire perf-sentinel contre une source configurée, produisant le
dashboard HTML autonome que rend le moteur.

- `POST /api/analyses` prend `{"source_id": "...", "request": {...}}` et répond `202` avec
  l'identifiant du run. La forme de la requête suit le kind de la source : `{}` pour un daemon, qui
  ne prend aucun paramètre, `{service, lookback | from_ms + to_ms, max_traces}` pour un backend de
  traces, ou `{trace_id}`. Les exclusions propres au moteur sont appliquées avant toute mise en
  file, donc une combinaison impossible est refusée plutôt que découverte trois minutes plus tard
  sous la forme d'un run échoué.
- `GET /api/analyses` liste les runs récents, le plus récent d'abord, et `GET /api/analyses/{id}`
  en renvoie un.
- `GET /reports/{id}.html` sert le rapport d'un run réussi, depuis la même origine que le reste.

Un run vaut deux invocations du moteur, parce que les sous-commandes de query émettent du texte,
du JSON ou du SARIF et que seule `report` écrit du HTML : la source est lue vers un JSON de
rapport, puis ce JSON est rendu. Un daemon saute la première étape, puisque son propre
`/api/export/report` en renvoie déjà un.

Les deux invocations tournent depuis `Hub:Analysis:ReportDirectory`. Le moteur cherche un
`.perf-sentinel.toml` relatif à son propre répertoire de travail, donc le laisser non réglé
permettrait à un fichier égaré, posé à côté du répertoire depuis lequel le Hub a été lancé, de
décider des seuils de détection de tous les runs.

Une requête peut porter un objet `detection` qui surcharge les seuils de détection du moteur
(`n_plus_one_min_occurrences`, `window_duration_ms`, `slow_query_threshold_ms`,
`slow_query_min_occurrences`, `max_fanout`, `chatty_service_min_calls`,
`pool_saturation_concurrent_threshold`, `serialized_min_sequential`). Les bornes reflètent le
validateur du moteur, et `GET /api/status` les publie avec chaque défaut sous `detection_knobs`.
Une valeur égale au défaut est écartée plutôt qu'enregistrée, de sorte qu'un run ne porte que ce
qui s'écarte réellement de la configuration standard. Les surcharges sont écrites dans un TOML
propre au run, remis aux deux invocations via `-c`, et supprimé à la fin du run.

Ces seuils décident de ce qui compte comme un problème, pas de la façon dont le rapport est écrit.
En relever un n'allège pas un run, cela empêche le détecteur de rapporter les cas les plus petits,
et c'est pourquoi les comptes de runs aux seuils différents ne sont pas comparables, ce que le
lanceur dit. Une source daemon n'en prend aucun : elle détecte avec sa propre configuration et le
Hub ne fait que lire ce qu'elle a déjà trouvé.

La taille du rapport n'est pas un réglage. La cible de 5 Mio du sink est une constante privée sans
flag, sans variable d'environnement et sans clé de configuration, et un rapport bâti depuis une
requête de backend plafonne autour de 4 Mo parce que la part de ce budget réservée aux arbres de
spans embarqués n'est jamais dépensée : une requête de backend renvoie des findings, pas des spans.
Quand le sink écarte effectivement des findings pour tenir, le run enregistre combien ont survécu,
relu depuis le fichier rendu, et le panneau de résultat le dit au-dessus du lien.

Chaque échec est rapporté sous l'un de huit codes (`source_unreachable`, `source_auth_failed`,
`source_rejected_request`, `timeout`, `output_too_large`, `binary_failed`, `invalid_request`,
`internal`). La sortie d'erreur brute ne quitte jamais le process. Elle est lue pour nommer un
responsable, puisque "le backend nous a refusés" et "le binaire a cassé" n'ont pas le même
responsable, et cette classification est une heuristique sur un ensemble borné de marqueurs, non un
contrat.

Les rapports sont supprimés `Hub:Analysis:ReportRetention` après leur succès et le run est marqué
expiré, en gardant ses paramètres. Ce n'est pas une piste d'audit, et un lien partagé hier est déjà
mort. Un run encore en cours à l'arrêt du service revient `interrupted` et n'est jamais rejoué de
lui-même : une reprise silencieuse lancerait une seconde requête lourde vers un backend que
personne n'a demandé d'interroger deux fois.

## Lanceur

Le Hub sert une interface de navigateur sur `/`, depuis la même origine que les rapports qu'elle
ouvre. HTML, CSS et JavaScript simples, sans framework, sans étape de build et sans requête
réseau : les deux polices sont en base64 dans `wwwroot/fonts.css` et chaque icône est un SVG en
ligne.

Quatre écrans : démarrer une analyse, suivre un run, lister les runs récents, et lire la santé de
la flotte. Le formulaire s'adapte au `kind` de la source sélectionnée plutôt que d'offrir un
sélecteur indépendant entre live et historique, puisqu'un tel sélecteur laisserait l'opérateur
composer des états impossibles, comme une fenêtre de trois heures contre un daemon qui en garde
dix minutes.

Une jauge est teintée dès qu'elle approche d'un plafond qu'elle a publié : rouge à partir de 90 %,
qui est la ligne du conseiller du moteur lui-même et celle qui fait passer le verdict de la ligne à
"près du plafond", et ambre à partir de 75 %, qui est le palier propre au Hub, un cran avant.
Chaque lecture montre aussi ce qui a bougé depuis la précédente, montant depuis le chiffre auquel
cela se rapporte puis s'effaçant, rouge pour une hausse et vert pour une baisse : chacun de ces
chiffres compte vers un plafond, donc la hausse est la direction qui coûte quelque chose. L'uptime
n'est ni teinté ni suivi, n'ayant pas de plafond et une seule direction possible.

Les replis qu'un lecteur a ouverts sont mémorisés dans le `localStorage` de ce navigateur, sous une
seule clé et comme replis ouverts seulement : une ligne, son bloc terminal, ses réglages et les
groupes qu'ils contiennent reviennent tous tels qu'ils ont été laissés, et une ligne laissée
ouverte relit son daemon à la visite suivante sans qu'on la clique. Rien d'autre que ces noms n'est
stocké, et un navigateur qui refuse le stockage démarre simplement tout replié.

Chaque commande imprimée porte un onglet par shell, parce que la différence n'est pas cosmétique :
un shell POSIX continue une ligne par une barre oblique inverse et échappe une apostrophe en
fermant puis rouvrant, PowerShell continue par un accent grave et double l'apostrophe, et son
ensemble de mots nus est plus étroit puisque la virgule y est l'opérateur de tableau. L'onglet
qu'ouvre une première visite suit la plateforme, Windows recevant PowerShell, et le choix propre du
lecteur est retenu ensuite et s'applique d'un coup à toutes les commandes de la page.

Aucune des deux commandes ne porte de valeur d'exemple. Le point d'accès est le `BaseUrl` configuré
de la source elle-même, et la commande du monitor porte l'intervalle de relecture que le lecteur a
choisi sur cette ligne, de sorte qu'une ligne copiée est exécutable telle quelle et ne contredit
pas l'écran d'où elle vient. La seule chose qu'un opérateur tape encore est le nom de service, qui
lui appartient et qui est montré vide plutôt que deviné.

Les deux commandes imprimées disent où obtenir le moteur, puisque ni l'une ni l'autre ne passe par
le Hub : la note lie la release de la version exacte que ce Hub fait tourner, qui est la version
pour laquelle les flags sont orthographiés. Sans version sondée, le lien retombe sur la liste des
releases plutôt que d'inventer un tag.

Le lanceur imprime aussi le run sous forme de ligne de commande du moteur, pour qu'un opérateur
puisse l'emporter dans un terminal. Elle est bâtie depuis l'objet même que le formulaire poste, et
jamais depuis le formulaire, de sorte que la commande imprimée et le run soumis ne peuvent pas
diverger. C'est une commande et non les deux que le Hub lance : la sortie JSON et l'étape de rendu
existent pour que le Hub puisse bâtir un dashboard, et un terminal n'a besoin ni de l'une ni de
l'autre. Les valeurs sont protégées pour un shell POSIX par des apostrophes simples, la seule forme
qui tienne pour un nom de service portant un `$` ou une apostrophe. Les surcharges de détection
n'ont pas de flag en ligne de commande, donc un run qui en a changé une porte
`-c perf-sentinel.toml` et le fichier est imprimé à côté de la commande, prêt à copier ou à
télécharger. Le nom est sans point parce que le moteur ne découvre que le `.perf-sentinel.toml`
pointé, qu'un téléchargement peut ne pas préserver, donc la commande nomme le fichier au lieu de
s'en remettre à cette découverte. Une source authentifiée imprime `--auth-header-env` plutôt que
son jeton, que le Hub détient et ne divulgue jamais.

Un rapport rendu depuis une source daemon devient live quand le `BaseUrl` de ce daemon est une
origine nue : le rendu passe `--daemon-url`, et les contrôles de rafraîchissement et
d'acquittement du dashboard parlent alors à ce daemon depuis le navigateur du lecteur. Deux
conditions se trouvent hors du Hub : le `[daemon.cors] allowed_origins` du daemon doit porter
l'origine depuis laquelle ce Hub sert ses rapports, et le lecteur doit pouvoir joindre le daemon
directement. Un daemon derrière un ingress à préfixe de chemin reçoit un rapport statique, parce
que le flag du moteur prend une origine et rien d'autre. Il en va de même de toute source daemon
quand le binaire configuré ne prend pas `--daemon-url` du tout : le moteur le déclare à l'intérieur
de sa feature `daemon`, donc un binaire bâti sans cette feature refuse l'argument au lieu de
l'ignorer, et un run qui le passerait ne rendrait rien. Le Hub interroge le binaire une fois au
démarrage, par `report --help`, et rend en statique quand la réponse est non ou illisible. Les
liens de rapport se partagent à portée réseau du Hub et meurent avec la fenêtre de rétention.

Sur l'écran de santé de la flotte, la ligne d'un daemon se déplie sur les jauges qu'il rapporte
face à leurs plafonds et les conseils qu'il écrit sur son propre tuning, dont `/metrics` ne porte
ni les uns ni les autres. La ligne relit à un intervalle que le lecteur choisit, le même réglage
que porte `query monitor --refresh` plus une position éteinte, et une lecture ne remplace que les
jauges et les conseils : les réglages ne changent pas sans redémarrage, donc les reconstruire
jetterait des groupes ouverts pour rien. Replier la ligne arrête les lectures. Les réglages
eux-mêmes sont un clic plus loin, groupés et repliés, chaque groupe montrant combien de ses valeurs
s'écartent des défauts du moteur. Cela se termine par la commande `perf-sentinel query monitor`
pour la même vue dans un terminal.

Le thème est à trois positions : système, clair, sombre. Seul le clair ou le sombre résolu atteint
le DOM, de sorte que les feuilles de style voient deux valeurs et jamais trois. La position est
stockée sous `perf-sentinel:theme` à la fois dans `localStorage` et dans `sessionStorage`, le
second parce que le dashboard rendu lit cette clé exacte depuis cette origine. C'est ce passage de
relais qui impose que le lanceur et les rapports partagent une origine.

Rien de ce qui vient du serveur n'est jamais écrit avec `innerHTML`. Chaque chaîne affichée est un
nœud de texte.

## Fraîcheur et rétablissement

Le push est primaire. Chaque source est aussi pollée indépendamment. Un poll réussi met à jour les
observations mais ne supprime pas un finding au seul motif qu'une réponse ultérieure du daemon
l'omet : le tampon circulaire du daemon peut l'avoir évincé, donc absent ne veut **pas** dire
résolu. La rétention retire les findings dont la dernière observation est plus ancienne que la
période configurée.

Le daemon perf-sentinel 0.11.x plafonne `/api/findings` à 1 000 lignes. Le Hub utilise ce plafond
exact et avertit chaque fois qu'il est atteint, parce que l'instantané du filet de sécurité peut
être incomplet. Une couverture à fort volume exige donc l'exportateur de push borné.

Les échecs laissent lisibles les findings déjà stockés. La source concernée est marquée injoignable
et réessayée avec un repli exponentiel borné, et un succès ultérieur efface cet état. Les corps de
poll sont limités à 16 Mio, les requêtes ont un timeout, les imports sont transactionnels, et les
logs n'identifient que l'identifiant de source et un code d'erreur stable.

## Sauvegarde et restauration

L'historique `first_seen` est la seule chose que le Hub stocke qu'aucun amont ne peut reconstruire :
le tampon circulaire du daemon oublie, donc perdre le volume perd la chronologie. Le binaire livre
une commande `backup` qui prend un instantané de la base vivante par le `VACUUM INTO` de SQLite,
sûr à côté de l'unique écrivain grâce au WAL. Elle lit `Hub:DatabasePath` depuis la même
configuration que le serveur, refuse d'écraser une destination existante, et retire son fichier
partiel quand la copie échoue. L'instantané est une copie complète écrite sur le même volume, donc
gardez au moins la taille de la base elle-même de libre sur le PVC, le défaut du chart étant de 1
Gio au total, avant d'en démarrer un.

```bash
kubectl exec deploy/perf-sentinel-hub -- /app/PerfSentinelHub backup /data/hub-backup-20260826.db
```

Datez le nom de fichier : le garde-fou contre l'écrasement fait échouer un chemin fixe au second
passage, et l'image d'exécution chiselée n'a pas de shell pour supprimer un reliquat. `kubectl cp`
ne peut pas non plus tirer le fichier depuis le pod du Hub, faute de tar à l'intérieur. Montez le
même PVC dans un pod éphémère, copiez depuis là, et retirez l'instantané de ce pod. Le volume est
`ReadWriteOnce`, donc épinglez l'assistant au nœud où tourne le pod du Hub, faites-le tourner sous
l'uid du Hub pour qu'il puisse lire et supprimer les fichiers, et donnez-lui le contexte de
sécurité qu'exige un namespace `restricted` :

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

En local, `make backup DB=/chemin/vers/hub.db DEST=backup.db` enveloppe la même commande. Restaurez
en remplaçant le fichier de base pendant que le Hub est complètement arrêté : ramenez le Deployment
à zéro et attendez que le pod se termine réellement avant de toucher au volume, un arrêt gracieux
pouvant prendre des dizaines de secondes
(`kubectl scale deploy/perf-sentinel-hub --replicas=0` puis
`kubectl wait --for=delete pod -l app.kubernetes.io/name=perf-sentinel-hub --timeout=120s`).
Ensuite, depuis le pod assistant, copiez la sauvegarde par-dessus `/data/hub.db`, supprimez les
`hub.db-wal` et `hub.db-shm` périmés à côté, et remontez l'échelle.

## Exclusions délibérées

La base de code actuelle n'a ni ingress, ni authentification d'utilisateur, ni import CI/SARIF, ni
interface humaine, ni écrivain d'acquittements, ni sauvegarde distante. La commande `backup` locale
prend un instantané de la base, expédier le fichier hors du cluster à intervalle régulier reste le
travail de l'opérateur. L'exposition réseau et l'authentification relèvent de la prochaine
conception indépendante, et les acquittements restent dans le dépôt que consomme perf-sentinel.

## Développement

```bash
make verify
make security
make release-check VERSION=0.1.0
```

Ce sont les points d'entrée locaux stables pour l'opérateur. `make verify` est l'équivalent local
de la porte agrégée de build et d'empaquetage, `make security` lance les contrôles de sécurité
configurés, et `make release-check VERSION=0.1.0` valide la cohérence des versions du dépôt avant
la création d'un tag signé.
Le check GitHub protégé est `CI / Gate`, issu de l'App dédiée PerfSentinel CI Gate. Un check
GitHub Actions portant le même nom ne satisfait pas cette frontière adossée à l'App.

La porte locale utilise des paquets verrouillés, les tests, une publication NativeAOT Linux,
Docker et Trivy, ainsi que le lint Helm. La chaîne d'outils est épinglée au SDK .NET 10.0.400, à
ASP.NET/SQLite 10.0.11, SQLitePCLRaw 3.0.5, Helm 4.2.3, et à des GitHub Actions épinglées par SHA :
checkout 7.0.1, setup-dotnet 6.0.0, setup-helm 5.0.1 et Trivy Action 0.36.0. Les conteneurs
d'exécution sont non-root, en lecture seule, et fondés sur des images officielles NativeAOT et
chiselées épinglées par digest.

## Vérifier une release publique en salle blanche

Après l'activation publique, partez d'un checkout neuf du tag stable et téléchargez chaque actif
depuis son URL exacte de GitHub Release dans un nouveau répertoire `release/`. Aucun secret de
dépôt n'est nécessaire :

```bash
python3 scripts/verify-release.py public-input \
  https://github.com/robintra/PerfSentinelHub/releases/tag/v0.1.0
python3 scripts/verify-release.py verify-published --root release
```

La première commande n'accepte qu'un tag de forme stable canonique ou une URL de release exacte.
Confirmez sur cette page que la release est publiée, et non brouillon ou préversion. La seconde
échoue fermée à moins que le répertoire téléchargé ne contienne exactement les actifs déclarés par
le manifeste et que chaque somme de contrôle, sujet, identité de source, digest d'image, liaison de
chart, SBOM, bundle de signature et bundle d'attestation ne concorde. Suivez
[RELEASING.md](RELEASING.md) pour les commandes publiques exactes de Cosign et d'attestation
GitHub. La vérification publique est configurée pour les quatre cibles, l'image par digest et le
chart par digest, et son observation réussie est différée à la répétition publique.

## Licence

[GNU Affero General Public License v3.0](LICENSE). Les applications et les greffons d'IDE
communiquent avec le Hub par HTTP plutôt qu'en le liant. Si vous modifiez le Hub et offrez cette
version modifiée sur un réseau, la section 13 de l'AGPL s'applique. Ceci est un résumé pratique et
non un avis juridique.
