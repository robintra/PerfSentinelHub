# API HTTP

Trois surfaces, et elles ne se recouvrent pas. Un daemon pousse vers l'API d'import. Un
greffon d'IDE ou un job de CI lit l'API de lecture. Le navigateur utilise l'API d'analyse
et n'appelle jamais `/api/findings`.

## API d'import

`POST /api/import/findings?source_id=<id>` accepte l'enveloppe du daemon
`{"producer_version":"…","findings":[…]}` avec `X-API-Key`. Une requête porte de 1 à 100
findings et 2 Mio au plus. La réponse n'est envoyée qu'une fois l'upsert idempotent par
signature commité.

Quatre imports tournent à la fois, ce qui borne la mémoire des requêtes indépendamment du
nombre de daemons. Les écritures sont sérialisées face aux chemins de poll et de
rétention. Un import qui ne peut pas prendre le verrou d'écriture en cinq secondes reçoit
`503 Retry-After: 1`, et les exportateurs des daemons conservent puis rejouent leurs lots
fusionnés. La rétention purge par tranches bornées, pour qu'une purge longue ne rejette
pas les imports pendant toute sa durée.

Un push met à jour les findings et les observations par source, rien d'autre. Il n'efface
jamais le `unreachable_since_ms` du chemin de poll. Une source que le Hub ne peut pas
joindre continue de rapporter `unreachable_since` alors que son daemon pousse avec succès,
et c'est juste : la joignabilité est un fait sur la route du Hub vers le daemon, qu'un
push n'exerce pas.

## API de lecture

| Endpoint                             | Renvoie                                                                                                                                                                                                         |
|--------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `GET /api/status`                    | La version du Hub, celle du moteur qu'il lancerait (`engine_version`, null quand aucun n'est configuré), et ce que coûte un run : workers, profondeur de file, plafond de traces, timeout, rétention de rapport |
| `GET /api/sources`                   | Chaque source configurée avec son kind et son dernier état de collecte connu                                                                                                                                    |
| `GET /api/findings`                  | Les findings, filtrés par `service`, `finding_type`, `severity`, `status`, `limit`, `include_acked`                                                                                                             |
| `GET /api/findings/{traceId}`        | Les findings d'une trace d'exemple                                                                                                                                                                              |
| `GET /api/sources/{sourceId}/daemon` | Les réglages appliqués d'un daemon et son propre compte rendu. Voir plus bas                                                                                                                                    |
| `GET /metrics`                       | Format texte Prometheus, voir [OPERATIONS-FR.md](OPERATIONS-FR.md#métriques)                                                                                                                                    |
| `GET /health/live`                   | Si le process est debout                                                                                                                                                                                        |
| `GET /health/ready`                  | Positif après l'initialisation de SQLite                                                                                                                                                                        |

Sur `/api/sources`, les horodatages sont null pour une source jamais observée, ce qu'un
lecteur ne doit pas confondre avec l'epoch. `producer_version` est null pour un backend de
traces, parce qu'un backend stocke des traces et ne détecte rien.

Sur `/api/findings`, `include_acked` vaut `true` par défaut. À `false`, il masque les
enveloppes portant un `acknowledged_by` non null.

### La vue daemon

`GET /api/sources/{sourceId}/daemon` lit à la demande plutôt que depuis le poll. Les
réglages ne changent jamais sans un redémarrage dont le Hub n'a aucun signal, et ce sont
les jauges qui font l'intérêt de la vue.

**Un échec est une observation, pas une panne.** Une source inconnue répond `404` et un
backend de traces `400`, puisqu'il ne fait tourner aucun daemon. Un daemon qui ne répond
pas est rapporté `state: "unreachable"` avec un code d'erreur, jamais en `502` : le Hub
relaie la santé d'une source, il n'échoue pas lui-même.

**Ce qu'il relaie verbatim.** `config` est la section `[daemon]` du daemon. Elle vaut null
avec `config_unavailable_reason: "api_disabled"` quand ce daemon ne sert aucune API de
query, ce qui est une affirmation de configuration et non une panne. `detection_config`,
`scoring_config` et `energy_model` viennent de l'export du daemon, là où `/api/config` ne
les porte pas. `warnings` est le conseiller de tuning du daemon lui-même : le Hub relaie
ces phrases et n'en écrit aucune.

**Ce qu'il borne.** Un conseil au-delà de deux mille caractères est coupé avec une ellipse
visible. Tout ce qui dépasse la centaine de conseils est compté dans `warnings_dropped`
plutôt que disparu en silence. Une lecture d'export ratée est nommée dans
`hints_unavailable_reason` au lieu de se lire comme un bulletin de santé vierge.

**Ce qu'il dérive.** Une seule chose : `state`, selon qu'une jauge a franchi 90 % de son
plafond, la même ligne que trace le monitor du daemon. Il porte aussi `daemon_defaults`,
`detection_defaults` et `defaults_engine_version`, pour qu'un lecteur puisse marquer ce
qu'un daemon a réellement changé. Ces défauts sont ceux du binaire que ce Hub embarque,
donc la version est nommée plutôt que supposée, et un daemon sur une autre mineure est
signalé plutôt que jugé.

**À quelle fréquence il lit.** Une ligne ouverte relit à l'intervalle que choisit son
lecteur. `?refresh=status` fait de ce tic une simple lecture de statut au lieu des trois
que prend la vue complète, l'export étant la lourde et ne tournant au plus qu'une fois par
minute. Une ligne dont la lecture a échoué relit avec cette même requête bon marché, et la
première qui répond est suivie aussitôt d'une lecture complète, de sorte qu'une ligne
laissée ouverte se rétablit d'elle-même.

Ce rythme ne peut pas affamer le daemon. Le plafond de 32 requêtes concurrentes du moteur
est cantonné à sa route d'ingestion OTLP précisément pour que `/api` et `/health` restent
réactifs, les tics de statut ne prennent aucune place de lecture au Hub, et les lectures
complètes sont bornées à deux à la fois sur des connexions mutualisées. La surface de
query du daemon est HTTP(S) seulement par conception, son port gRPC étant l'ingestion
OTLP, donc le Hub ne lui parle aucun RPC.

### Ce que le Hub ajoute à un finding

Chaque finding du daemon est conservé comme un document JSON opaque et additif. Le Hub y
ajoute `first_seen`, `last_seen`, `max_confidence`, `status`, un `lineage` optionnel, et
la fraîcheur de la source. Les clients d'IDE doivent ignorer les champs inconnus, comme
ils le font avec l'API du daemon.

`first_seen` vient de l'enveloppe du daemon (`first_seen_ms`), borné à l'heure
d'observation du Hub et à un plancher de bon sens en millisecondes Unix. Ni une horloge de
daemon en avance ni un bug d'unité en secondes ne peuvent le fausser, et il retombe sur
l'heure d'observation quand un producteur omet le champ.

`last_seen` est délibérément l'horloge d'observation du Hub. La rétention, l'ordonnancement
et les comparaisons de fraîcheur s'appuient dessus, donc il ne vient jamais d'une horloge
distante.

### Comment `status` est dérivé

Dérivé à la lecture, jamais stocké, depuis des données que le Hub garde déjà :

| Valeur            | Sens                                                                    |
|-------------------|-------------------------------------------------------------------------|
| `active`          | Vu dans le délai `Hub:ResolutionGrace`, 7 jours par défaut              |
| `likely_resolved` | S'est tu, mais son endpoint bat encore depuis une source joignable      |
| `not_observed`    | Rien ne prouve rien : un endpoint silencieux, ou une flotte injoignable |

C'est une présomption et non un verdict. Un finding qui part par rétention part toujours
en silence, mais un lecteur peut désormais distinguer "l'endpoint tourne sans le finding"
de "personne ne regarde". `?status=<valeur>` filtre, et le filtre s'applique avant la
limite de page.

### Filiation

`first_seen` vaut par signature, donc un finding dont le template normalisé change reçoit
une nouvelle signature et un nouveau `first_seen`. Depuis le schéma v2, le Hub relie une
telle mutation à son prédécesseur à l'import, quand exactement un finding stocké :

- partage le service, le détecteur et l'endpoint,
- porte un hash de template différent,
- a été vu dans les 30 derniers jours et strictement avant le lot entrant,
- et n'est pas lui-même déjà supplanté.

L'ambiguïté n'enregistre rien, nommer l'un de plusieurs candidats serait une supposition.

Une enveloppe reliée porte un objet `lineage` avec `original_first_seen`, la naissance la
plus ancienne de la chaîne, et `predecessors`, sa longueur. Les deux sont dénormalisés sur
le maillon le plus récent, de sorte que la filiation complète survit à la purge par
rétention de chaque étape antérieure. L'heuristique est conservatrice et non destructive :
les deux lignes restent des findings distincts, et le prédécesseur vieillit par la
rétention ordinaire.

## API d'analyse

Une analyse est un run du binaire perf-sentinel contre une source configurée, produisant
le dashboard HTML autonome que rend le moteur.

| Endpoint                 | Fait                                                                                   |
|--------------------------|----------------------------------------------------------------------------------------|
| `POST /api/analyses`     | Prend `{"source_id": "...", "request": {...}}`, répond `202` avec l'identifiant du run |
| `GET /api/analyses`      | Liste les runs récents, le plus récent d'abord                                         |
| `GET /api/analyses/{id}` | Renvoie un run                                                                         |
| `GET /reports/{id}.html` | Sert le rapport d'un run réussi, depuis la même origine que le reste                   |

La forme de la requête suit le kind de la source : `{}` pour un daemon, qui ne prend aucun
paramètre, `{service, lookback | from_ms + to_ms, max_traces}` pour un backend de traces,
ou `{trace_id}`. Les exclusions propres au moteur sont appliquées avant toute mise en
file, donc une combinaison impossible est refusée plutôt que découverte trois minutes plus
tard sous la forme d'un run échoué.

### Deux invocations, pas une

Les sous-commandes de query émettent du texte, du JSON ou du SARIF, et seule `report`
écrit du HTML. La source est donc lue vers un JSON de rapport, puis ce JSON est rendu. Une
source daemon saute la première étape, son propre `/api/export/report` en renvoyant déjà
un.

Les deux invocations tournent depuis `Hub:Analysis:ReportDirectory`. Le moteur cherche un
`.perf-sentinel.toml` relatif à son propre répertoire de travail, donc le laisser non
réglé permettrait à un fichier égaré, posé à côté du répertoire depuis lequel le Hub a été
lancé, de décider des seuils de détection de tous les runs.

### Surcharges de détection

Une requête peut porter un objet `detection` qui surcharge les seuils du moteur :
`n_plus_one_min_occurrences`, `window_duration_ms`, `slow_query_threshold_ms`,
`slow_query_min_occurrences`, `max_fanout`, `chatty_service_min_calls`,
`pool_saturation_concurrent_threshold`, `serialized_min_sequential`.

Les bornes reflètent le validateur du moteur, et `GET /api/status` les publie avec chaque
défaut sous `detection_knobs`. Une valeur égale au défaut est écartée plutôt
qu'enregistrée, de sorte qu'un run ne porte que ce qui s'écarte de la configuration
standard. Les surcharges sont écrites dans un TOML propre au run, remis aux deux
invocations via `-c`, et supprimé à la fin du run.

Ces seuils décident de ce qui compte comme un problème, pas de la façon dont le rapport
est écrit. En relever un n'allège pas un run, cela empêche le détecteur de rapporter les
cas les plus petits. C'est pourquoi les comptes de runs aux seuils différents ne sont pas
comparables, et pourquoi le lanceur le dit. Une source daemon n'en prend aucun : elle
détecte avec sa propre configuration, et le Hub ne fait que lire ce qu'elle a trouvé.

### Taille du rapport

Ce n'est pas un réglage. La cible de 5 Mio du sink est une constante privée, sans flag,
sans variable d'environnement et sans clé de configuration. Un rapport bâti depuis une
requête de backend plafonne autour de 4 Mo, parce que la part de ce budget réservée aux
arbres de spans embarqués n'est jamais dépensée : une requête de backend renvoie des
findings, pas des spans. Quand le sink écarte effectivement des findings pour tenir, le
run enregistre combien ont survécu, relu depuis le fichier rendu, et le panneau de
résultat le dit au-dessus du lien.

### Échec et expiration

Chaque échec est l'un de huit codes : `source_unreachable`, `source_auth_failed`,
`source_rejected_request`, `timeout`, `output_too_large`, `binary_failed`,
`invalid_request`, `internal`.

La sortie d'erreur brute ne quitte jamais le process. Elle est lue pour nommer un
responsable, puisque "le backend nous a refusés" et "le binaire a cassé" n'ont pas le même
responsable, et cette classification est une heuristique sur un ensemble borné de
marqueurs plutôt qu'un contrat.

Les rapports sont supprimés `Hub:Analysis:ReportRetention` après leur succès, et le run
est marqué expiré en gardant ses paramètres. Ce n'est pas une piste d'audit, et un lien
partagé hier est déjà mort.

Un run encore en cours à l'arrêt du service revient `interrupted` et n'est jamais rejoué
de lui-même. Une reprise silencieuse lancerait une seconde requête lourde vers un backend
que personne n'a demandé d'interroger deux fois.
