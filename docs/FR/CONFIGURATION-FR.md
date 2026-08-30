# Configuration

Les variables d'environnement suivent la forme .NET `Hub__...`. Helm expose les mêmes
réglages sous `hub` et `sources`.

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
| `Hub:Analysis:Timeout`           | `00:05:00`                                            | Positive, une heure au plus                                                                           |
| `Hub:Analysis:ReportRetention`   | `1.00:00:00` (24 heures)                              | Durée positive                                                                                        |
| `Hub:UpdateCheck:Enabled`        | `true`                                                | Si le Hub demande à GitHub la release publiée la plus récente                                         |
| `Hub:UpdateCheck:Interval`       | `1.00:00:00` (1 jour)                                 | Au moins 15 minutes                                                                                   |
| `Hub:UpdateCheck:EngineEndpoint` | API des releases GitHub de `robintra/perf-sentinel`   | HTTPS absolue, sans identifiants, ni query, ni fragment                                               |
| `Hub:UpdateCheck:HubEndpoint`    | API des releases GitHub de `robintra/PerfSentinelHub` | HTTPS absolue, sans identifiants, ni query, ni fragment                                               |
| `Hub:Sources`                    | aucune                                                | Au moins une source                                                                                   |

## Réglages par source

| Réglage                          | Défaut   | Validation                                                                                                          |
|----------------------------------|----------|---------------------------------------------------------------------------------------------------------------------|
| `Sources[].Id`                   | aucun    | Non vide et unique                                                                                                  |
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
