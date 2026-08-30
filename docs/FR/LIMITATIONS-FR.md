# Limites

Tout ce qui suit est déjà écrit ailleurs dans ces documents, à côté de la
fonction que cela contraint. Cette page les rassemble pour qu'on puisse lire les
bornes d'un seul tenant avant de déployer, plutôt que de les découvrir une par
une.

## Ce que le Hub déclare au lieu de le mesurer

`Environment` et `RetentionHours` sont repris de la configuration tels quels et
ne sont confrontés à rien. Un déploiement mal configuré peut étiqueter de la
production en staging sans que rien ne le contredise. Le lanceur marque la
moitié déclarée d'une ligne par un contour en pointillé. Voir
[CONFIGURATION-FR.md](CONFIGURATION-FR.md).

Un backend de traces n'est jamais interrogé. Seul un daemon l'est, donc une
source Tempo ou d'API de requêtage Jaeger n'affiche ni version de producteur ni
dernier succès. C'est la conception, pas un défaut.

## Ce qu'un poll peut dire, et ce qu'il ne peut pas

Le daemon perf-sentinel 0.11.x plafonne `/api/findings` à 1 000 lignes. Le Hub
utilise exactement ce plafond et avertit dès qu'il est atteint, parce que
l'instantané peut être incomplet. Une couverture à fort volume passe par
l'exportateur push borné, pas par le poll.

Un poll qui omet un finding ne le résout pas. Le tampon circulaire du daemon
peut simplement l'avoir évincé, et absent n'est pas la même chose que disparu.
Seule la rétention retire une ligne.

La joignabilité est à sens unique. Un daemon qui pousse avec succès prouve
qu'il peut joindre le Hub, pas que le Hub peut le joindre, et seul un poll
réussi efface `unreachable_since_ms`. Une source dont le push arrive alors que
son poll échoue continue de rapporter `unreachable_since`. Voir
[OPERATIONS-FR.md](OPERATIONS-FR.md).

## Ce qu'affirment `status` et `lineage`

`status` est une présomption, pas un verdict. Il est dérivé à la lecture depuis
des données que le Hub détient déjà, et `likely_resolved` signifie que
l'endpoint bat encore sans le finding, pas que quelqu'un l'a corrigé.

Le lignage relie une signature mutée à sa devancière seulement quand exactement
un finding stocké correspond. L'ambiguïté n'enregistre rien, parce que nommer
l'un de plusieurs candidats serait une supposition. Voir [API-FR.md](API-FR.md).

## Ce qu'un run ne promet pas

Les rapports sont supprimés après `Hub:Analysis:ReportRetention`, 24 heures par
défaut. Ce n'est pas une piste d'audit, et un lien partagé hier est déjà mort.
Le run garde ses paramètres, donc il peut être relancé tel quel.

Un run encore en cours à l'arrêt du service revient en `interrupted` et n'est
jamais rejoué de lui-même. Une reprise silencieuse lancerait une seconde requête
lourde vers un backend que personne n'a demandé d'interroger deux fois.

La taille d'un rapport n'est pas un réglage. La cible de 5 Mio du sink est une
constante privée, sans option, sans variable d'environnement et sans clé de
configuration. Quand il écarte des findings pour tenir, le run enregistre
combien ont survécu, relu depuis le fichier rendu.

Les huit codes d'échec viennent d'une heuristique sur un jeu borné de marqueurs
dans la sortie d'erreur du moteur, pas d'un contrat. La sortie d'erreur brute ne
quitte jamais le processus.

Les comptes de runs aux seuils de détection différents ne sont pas comparables.
Relever un seuil n'allège pas un run, il empêche le détecteur de rapporter les
cas les plus petits.

## Ce qui se joue hors du Hub

Un rapport vivant exige deux choses que le Hub ne contrôle pas : le
`[daemon.cors] allowed_origins` du daemon doit porter l'origine depuis laquelle
le Hub sert ses rapports, et le lecteur doit pouvoir joindre ce daemon
directement. Un daemon derrière un ingress à préfixe de chemin reçoit un rapport
statique à la place, parce que le `--daemon-url` du moteur prend une origine et
rien d'autre. Voir [LAUNCHER-FR.md](LAUNCHER-FR.md).

## Échelle et observabilité

Une seule réplique, et ce n'est pas un réglage. SQLite n'a qu'un écrivain et le
volume est `ReadWriteOnce`, donc le chart pose `replicas: 1`.

Le Hub n'expose aucun endpoint Prometheus. `/health/live`, `/health/ready` et
`GET /api/status` constituent toute la surface opérationnelle. Le `/metrics`
propre à un daemon n'est pas affecté, le Hub n'en ajoute simplement pas un.
