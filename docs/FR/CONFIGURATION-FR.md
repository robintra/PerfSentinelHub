# Configuration

Les variables d'environnement suivent la forme .NET `Hub__...`. Helm expose les mêmes
réglages sous `hub` et `sources`.

[`examples/appsettings.reference.json`](../../examples/appsettings.reference.json) fixe
chaque réglage à la valeur que le Hub utilise déjà, annotée. La copier entièrement ne
change rien, c'est l'inventaire plutôt qu'un point de départ. Un test la maintient
exhaustive, ce qui compte ici parce que .NET ignore en silence une clé de configuration
qu'il ne reconnaît pas : un nom mal orthographié ne produit aucune erreur et se lit comme
un bug du Hub plutôt que comme une faute de frappe dans votre fichier.

## Réglages

| Réglage                          | Défaut                                                | Validation                                                                                            |
|----------------------------------|-------------------------------------------------------|-------------------------------------------------------------------------------------------------------|
| `Hub:DatabasePath`               | `/data/hub.db`                                        | Chemin absolu                                                                                         |
| `Hub:PollInterval`               | `01:00:00`                                            | Durée positive                                                                                        |
| `Hub:HttpTimeout`                | `00:00:10`                                            | Durée positive                                                                                        |
| `Hub:MaxConcurrentPolls`         | `4`                                                   | 1 à 32                                                                                                |
| `Hub:Retention`                  | `180.00:00:00` (180 jours)                            | Durée positive                                                                                        |
| `Hub:ResolutionGrace`            | `7.00:00:00` (7 jours)                                | Positive, inférieure à `Retention`                                                                    |
| `Hub:DefaultReadLimit`           | `1000`                                                | 1 à `MaxReadLimit`                                                                                    |
| `Hub:MaxReadLimit`               | `10000`                                               | 1 à 10000                                                                                             |
| `Hub:Analysis:EngineBinaryPath`  | aucun                                                 | Optionnel, chemin absolu vers le binaire perf-sentinel. Absent, les runs d'analyse sont indisponibles |
| `Hub:Analysis:ReportDirectory`   | `/data/reports`                                       | Absolu, accessible en écriture. Les rapports rendus vivent ici                                        |
| `Hub:Analysis:IdentityHeader`    | `X-Forwarded-User`                                    | En-tête qu'un reverse proxy renseigne avec l'identité du demandeur                                    |
| `Hub:Analysis:Workers`           | `2`                                                   | 1 à 16                                                                                                |
| `Hub:Analysis:MaxTracesCap`      | `2000`                                                | 1 à 10000, la limite propre du moteur sur `--max-traces`                                              |
| `Hub:Analysis:MaxTracesEmbedded` | `50`                                                  | 0 à 10000. Arbres de spans embarqués dans le rapport. Le poser fait sortir le sink du ciblage de taille |
| `Hub:Analysis:Timeout`           | `00:05:00`                                            | Positive, une heure au plus                                                                           |
| `Hub:Analysis:ReportRetention`   | `1.00:00:00` (24 heures)                              | Durée positive                                                                                        |
| `Hub:Analysis:RunRetention`      | `30.00:00:00` (30 jours)                              | Positive, plus longue que `ReportRetention`. Quand la ligne d'un run terminé est supprimée             |
| `Hub:UpdateCheck:Enabled`        | `true`                                                | Si le Hub demande à GitHub la release publiée la plus récente                                         |
| `Hub:UpdateCheck:Interval`       | `1.00:00:00` (1 jour)                                 | Au moins 15 minutes                                                                                   |
| `Hub:UpdateCheck:EngineEndpoint` | API des releases GitHub de `robintra/perf-sentinel`   | HTTPS absolue, sans identifiants, ni query, ni fragment                                               |
| `Hub:UpdateCheck:HubEndpoint`    | API des releases GitHub de `robintra/PerfSentinelHub` | HTTPS absolue, sans identifiants, ni query, ni fragment                                               |
| `Hub:Sources`                    | aucune                                                | Au moins une source                                                                                   |

## Réglages par source

| Réglage                          | Défaut   | Validation                                                                                                          |
|----------------------------------|----------|---------------------------------------------------------------------------------------------------------------------|
| `Sources[].Id`                   | aucun    | Unique, 1 à 64 caractères ASCII alphanumériques, `.`, `_` ou `-`                                                                                                  |
| `Sources[].Name`                 | aucun    | Non vide                                                                                                            |
| `Sources[].Environment`          | aucun    | Non vide                                                                                                            |
| `Sources[].Kind`                 | `daemon` | L'un de `daemon`, `tempo`, `jaeger_query`. Seul un daemon est pollé, et seul un daemon peut porter une clé d'import |
| `Sources[].RetentionHours`       | aucun    | Backends de traces seulement, d'une heure à dix ans                                                                 |
| `Sources[].BaseUrl`              | aucune   | Obligatoire. HTTP(S) absolue, sans identifiants, query ni fragment                                                  |
| `Sources[].AuthHeaderName/Value` | aucun    | Les deux absents ou les deux présents, sans saut de ligne                                                           |
| `Sources[].ImportApiKey`         | aucune   | Identifiant de push optionnel, au moins 32 caractères, fourni via un Secret                                         |

`RetentionHours` est déclaré et non mesuré, aucune API de backend ne l'exposant. Il porte
la même réserve que `Environment` : il garde une affirmation périmée jusqu'à ce que
quelqu'un l'édite.

`BaseUrl` conserve un préfixe de chemin, donc `https://gw/perf-sentinel/` polle
`https://gw/perf-sentinel/api/status`.

## Ce qu'est une source, et ce qui est mesuré

La liste est de la configuration, jamais une découverte. Rien n'est détecté
automatiquement, le lanceur ne peut pas ajouter de source, et le Hub refuse de démarrer
sans aucune.

Cela coupe chaque ligne de l'écran de flotte en deux. `Id`, `Name`, `Environment`, `Kind`,
`BaseUrl` et `RetentionHours` sont déclarés : repris de ce fichier tels quels et jamais
confrontés à quoi que ce soit. `reachable`, `last_success`, `unreachable_since`,
`producer_version` et `last_error` sont observés, écrits par le poll. Un contour en
pointillé marque la moitié déclarée dans le lanceur, et c'est pourquoi un déploiement mal
configuré peut étiqueter de la production en staging sans que rien ne le contredise.

Seul un daemon est interrogé. Son `api/status` fournit `producer_version`, et son
`api/findings` est le filet derrière le push.

Où cela se déclare : `Hub:Sources` dans `appsettings.json`, les variables d'environnement
`Hub__Sources__N__*`, ou `sources[]` dans les valeurs Helm. Les trois sont le même
réglage, et `Kind` décide dans quelle moitié de l'écran une ligne atterrit.

```yaml
sources:
  - id: checkout-prod
    name: Checkout production
    environment: production
    kind: daemon
    baseUrl: http://perf-sentinel.observability:4318
    importSecretName: hub-import-keys    # le secret de push, jamais en clair
    importSecretKey: checkout-prod
  - id: victoria-eu
    name: Victoria Traces EU
    environment: staging
    kind: jaeger_query                   # Victoria Traces parle l'API de requêtage Jaeger
    baseUrl: http://victoria-traces.observability:10428
    retentionHours: 72
```

La même paire en variables d'environnement, un indice par source :

```bash
Hub__Sources__0__Id=checkout-prod
Hub__Sources__0__Kind=daemon
Hub__Sources__0__BaseUrl=http://perf-sentinel.observability:4318
Hub__Sources__1__Id=victoria-eu
Hub__Sources__1__Kind=jaeger_query
Hub__Sources__1__BaseUrl=http://victoria-traces.observability:10428
Hub__Sources__1__RetentionHours=72
```

Un backend de traces n'est jamais contacté tant que personne ne lance d'analyse : aucune
route du Hub ne lit un Tempo. Le Hub ne fait que lancer le moteur contre lui, avec la
sous-commande qu'implique son type, `tempo` pour `tempo` et `jaeger-query` sinon. C'est
pourquoi une telle source n'affiche ni version de producteur ni dernier succès, et ce
n'est pas un défaut.

## Où le Hub se connecte

Chaque requête sortante va vers un `Sources[].BaseUrl` configuré, avec une exception. Une
fois par jour, le Hub demande à l'API des releases GitHub la version publiée la plus
récente du moteur et de lui-même, pour que la pastille de version puisse dire que ce que
vous faites tourner n'est plus le plus récent. C'est un GET non authentifié, qui ne porte
aucun identifiant de votre déploiement.

Pour un déploiement sans sortie réseau, réglez `Hub:UpdateCheck:Enabled` à `false`. La
pastille n'affiche alors rien plutôt que d'affirmer que vous êtes à jour, ce qu'elle
affiche aussi quand la requête échoue.

## Une source https avec une CA privée

Le Hub valide le certificat d'une source contre le magasin de confiance du conteneur. Un
daemon en cluster portant un certificat auto-signé ou émis en interne est refusé avec
`PartialChain` tant que sa CA n'y est pas.

L'image d'exécution est chiselée et n'a pas de shell, donc `update-ca-certificates` n'y
est pas exécutable. Pointez plutôt `SSL_CERT_FILE` vers un bundle monté depuis une
ConfigMap :

```yaml
env:
  - name: SSL_CERT_FILE
    value: /etc/perf-sentinel-hub/certs/bundle.crt
```

Le bundle doit être les racines publiques et votre CA concaténées, dans cet ordre, et non
la CA seule. La variable remplace le fichier par défaut au lieu de s'y ajouter, et un Hub
qui ne fait confiance qu'à votre CA ne peut rien joindre sur l'internet public.

Vérifié contre l'exécution sur laquelle cette image est bâtie : sans la variable le
certificat privé est refusé et le TLS public fonctionne, avec le bundle concaténé les deux
fonctionnent.
