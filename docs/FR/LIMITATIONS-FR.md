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

Le Hub relève les jours où chaque source a observé un finding, et ce relevé
commence le jour où la version qui l'écrit est déployée. Rien n'est rempli
après coup : pour un jour antérieur, le Hub ne détient que le `first_seen` et le
`last_seen` d'un finding, pas les jours entre les deux. La copie d'un finding
propre à une source commence de la même façon, à sa première observation après
la mise à niveau.

Une fenêtre `from` et `to` sur `/api/findings` retient des jours entiers, coupés
à minuit UTC sur l'horloge du Hub, donc une fenêtre plus étroite qu'un jour liste
quand même ce qu'une source a observé à n'importe quelle heure de ce jour. La
présence est supposée là où rien n'a été relevé : entre le `first_seen` d'un
finding pour une source et le premier jour que le Hub a relevé pour elle, et
jusqu'à son `last_seen` pour une paire sans aucun jour relevé, ce qui est le cas
de toutes les paires d'une base antérieure au relevé. Sans cette règle, un Hub
mis à niveau se lirait vide sur tout son passé. Avec elle, un finding disparu
puis revenu avant son premier jour relevé se lit comme continu.

La rétention (`Hub:Retention`) retire un jour relevé alors que le finding
lui-même peut rester, et le premier jour relevé ne bouge pas. Une fenêtre qui
tombe tout entière sur des jours retirés, après ce premier jour, ne liste donc
rien, même pour un finding présent tout du long.

Le Hub reflète les acquittements actifs de chaque daemon et n'en est jamais le
propriétaire. Le miroir a la fraîcheur du dernier poll de ce daemon, sauf si un
acquittement relayé par le Hub l'a rafraîchi depuis, donc un acquittement pris
ou révoqué directement sur le daemon apparaît au poll suivant. Un daemon
antérieur à 0.24.0 n'est jamais interrogé, parce qu'il ne sait pas lister sa
baseline de CI, et ses findings sont jugés sur leur seule enveloppe, comme
avant.

Un daemon qui liste 1 000 acquittements ou plus a atteint son propre plafond, et
le Hub consigne cette lecture `truncated`. Les acquittements qu'il a listés
apparaissent dans `acks`, mais une liste dont la fin manque ne peut pas dire
qu'un finding n'est pas acquitté, donc elle ne décide pas de
`include_acked=false` et cette source retombe sur l'enveloppe.

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

## Ce que le Hub n'authentifie pas

Cette section décrit le Hub avec `Hub:Auth:Enabled` désactivé, le défaut. Activé,
le Hub connecte lui-même les utilisateurs du navigateur et seules les routes
machines restent ouvertes, voir [AUTHENTICATION-FR.md](AUTHENTICATION-FR.md).

Un seul endpoint réclame une preuve, `POST /api/import/findings`, dont la
`X-API-Key` est comparée par empreinte. Tous les autres ne réclament rien :
`/api/status`, `/api/sources`, `/api/findings`, `/api/analyses` aussi bien pour
la liste que pour la requête qui lance un run, `/reports/`, `/metrics`, et le
lanceur lui-même. Qui joint le port lit tous les findings de tous les tenants,
modèles de requêtes SQL et noms d'endpoints compris.

Le `by` et le `reason` d'un acquittement reflété sont lisibles sur la route de
lecture ouverte, `/api/findings`, qui reste ouverte aux machines quand
`Hub:Auth` est activé. C'est qui a acquitté un finding et pourquoi, dans les
mots saisis sur le daemon, comme l'était déjà `acknowledged_by`.

L'en-tête d'identité attribue, il n'authentifie pas.
`Hub:Analysis:IdentityHeader` est enregistré sur un run comme une déclaration
faite par un proxy, et le Hub n'en vérifie rien. C'est le bon comportement
derrière un proxy authentifiant, et aucune défense sans lui. Si le réseau n'est
pas la frontière, posez ce proxy devant ou activez `Hub:Auth`. Voir
[DEPLOYMENT-FR.md](DEPLOYMENT-FR.md).

## Ce qui se joue hors du Hub

Un rapport vivant exige deux choses que le Hub ne contrôle pas : le
`[daemon.cors] allowed_origins` du daemon doit porter l'origine depuis laquelle
le Hub sert ses rapports, et le lecteur doit pouvoir joindre ce daemon
directement, à son `PublicUrl` quand le Hub le lit par un nom que seul le cluster
résout. Un daemon derrière un ingress à préfixe de chemin reçoit un rapport
statique à la place, parce que le `--daemon-url` du moteur prend une origine et
rien d'autre. Voir [LAUNCHER-FR.md](LAUNCHER-FR.md).

Le Hub porte la cousine de cette contrainte et doit lui-même être servi à la
racine d'une origine. Le lanceur appelle `/api/status`, `/api/sources`,
`/api/analyses` et `/reports/` en chemins absolus, rien ne les réécrit, et un
ingress à préfixe de chemin laisse le navigateur réclamer un préfixe auquel le
Hub ne répond jamais. Voir [DEPLOYMENT-FR.md](DEPLOYMENT-FR.md).

## Échelle et observabilité

Une seule réplique, et ce n'est pas un réglage. SQLite n'a qu'un écrivain et le
volume est `ReadWriteOnce`, donc le chart pose `replicas: 1`.

`GET /metrics` couvre la joignabilité, la file d'analyses, les comptes de runs
et les findings stockés de chaque environnement, et rien d'autre. Les findings
sont comptés par environnement, type, sévérité et statut, jamais par service ni
par endpoint, et les comptes ont jusqu'à 15 secondes. Aucune série ne porte la
durée d'une purge de rétention ni le débit d'import, donc une alerte sur ces
points, ou sur les findings d'un seul service, doit lire `/api/findings` ou les
journaux. Voir [OPERATIONS-FR.md](OPERATIONS-FR.md#métriques).
