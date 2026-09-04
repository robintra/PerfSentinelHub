# Formes de déploiement

Une question à laquelle le reste de cette documentation ne répond pas : quand
les environnements sont cloisonnés les uns des autres, faut-il un Hub par
cluster ou un Hub central qui les collecte tous. Cette page dit les flux
qu'exige chaque forme et ce que la moins chère abandonne, pour que la réponse
se lise avant de déployer quoi que ce soit.

## Chaque flux, et dans quel sens il va

| Flux                       | Sens    | Ce qu'il porte                                                                 |
|----------------------------|---------|--------------------------------------------------------------------------------|
| daemon vers Hub            | entrant | le chemin principal des findings, `POST /api/import/findings` avec `X-API-Key` |
| Hub vers daemon            | sortant | le poll, la joignabilité, et `api/export/report` au lancement d'un run         |
| Hub vers backend de traces | sortant | pendant un run, et jamais autrement                                            |
| navigateur vers Hub        | entrant | le lanceur et les rapports qu'il ouvre                                         |
| greffon d'IDE ou job de CI | entrant | `GET /api/findings`, rien d'autre                                              |
| Hub vers api.github.com    | sortant | la vérification de version, que `Hub:UpdateCheck:Enabled` désactive            |

Le Hub n'initie jamais rien vers une CI. Un build lance le moteur en mode
batch, il n'y a pas de daemon dedans, donc rien à interroger. La même topologie
est dessinée dans [ARCHITECTURE-FR.md](ARCHITECTURE-FR.md).

## Trois formes, et le prix de chacune

| Forme                  | Réseau à ouvrir       | Ce qui fonctionne                                                                           | URL pour les clients machine |
|------------------------|-----------------------|---------------------------------------------------------------------------------------------|------------------------------|
| un Hub par cluster     | rien                  | tout                                                                                        | une par environnement        |
| Hub central, deux sens | deux flux par cluster | tout                                                                                        | une seule                    |
| Hub central, push seul | un flux par cluster   | findings oui, joignabilité et dépliage d'une rangée daemon et run sur une source daemon non | une seule                    |

Un Hub déployé dans le cluster qu'il collecte joint ses daemons et ses backends
de traces en ClusterIP, donc la première forme n'ouvre aucune règle de
pare-feu. Elle le paie en adresses : chaque client machine doit en connaître
une par environnement, et aucun écran ne montre la flotte entière d'un seul
tenant.

Un Hub central inverse ce marché. Une adresse, un écran de flotte, et un jeu de
règles de pare-feu par cluster à maintenir. Ouvrir les deux sens conserve
toutes les fonctions. N'ouvrir que le sens entrant coûte moins cher et coûte
les fonctions listées plus bas, ce qui est un vrai choix et non une
installation dégradée.

## Ce qu'un poll rattrape, et ce qu'il ne peut pas

Le Hub interroge `api/findings?limit=1000&include_acked=true` immédiatement au
démarrage puis à chaque `Hub:PollInterval`. Cela resynchronise le contenu
courant du tampon du daemon. Ce n'est pas un journal. Ce que le tampon a évincé
pendant l'absence du Hub est perdu, et aucune limite ne le ramène. Un poll qui
revient pile sur le plafond est journalisé comme possiblement tronqué, parce
que l'instantané peut être en deçà de ce que le daemon détient. Voir
[LIMITATIONS-FR.md](LIMITATIONS-FR.md).

## Ce que le push seul abandonne

Quatre choses exigent que le Hub joigne le daemon, et non l'inverse. La
joignabilité est écrite par le poll et par rien d'autre, donc une source en
push seul n'affiche aucun dernier succès. Déplier une rangée daemon dans
l'écran de flotte lit cette source à la demande. Lancer un run sur une source
daemon commence par lui demander `api/export/report`. Et un finding qui a cessé
de récidiver pendant une coupure n'est plus jamais poussé, parce que seule la
récidive le pousse.

Une chose borne la gravité de tout cela. L'exportateur du daemon pousse une
signature dès sa découverte ou dès que sa sévérité empire, puis en rafraîchit une
encore active au plus une fois par heure, donc un problème persistant revient de
lui-même dans l'heure, poll ou pas. Ce que le poll aurait récupéré,
c'est le finding qui s'est tu, et la fenêtre entre un redémarrage et le
prochain rafraîchissement naturel.

## Quand un Hub central ne joint pas un backend

Rien ne devient inanalysable. Le lanceur imprime chaque run sous forme de ligne
de commande du moteur, donc un backend que le Hub central ne joint pas reste
analysable depuis un terminal qui, lui, le joint. Voir
[LAUNCHER-FR.md](LAUNCHER-FR.md).

## Ce qui doit être vrai de l'adresse

Le Hub doit être servi à la racine d'une origine, et il n'authentifie aucun de
ses lecteurs. Les deux contraintes façonnent le proxy inverse posé devant lui
plutôt que le Hub lui-même, donc à lire avant d'écrire l'ingress. Voir
[LIMITATIONS-FR.md](LIMITATIONS-FR.md).

## La CI lit, elle n'alimente pas

Un build produit du SARIF et du JSON, et ni l'un ni l'autre n'a sa place dans
le Hub. `last_seen` est l'horloge d'observation du Hub, `status` est déduit
d'un endpoint qui bat encore, et un build est un tir synthétique sur une
branche. En importer un rendrait `status` faux et ferait passer les findings
d'une branche pour ceux de la production. Un job de CI qui veut l'état de la
flotte appelle `GET /api/findings` comme n'importe quel autre lecteur. Voir
[API-FR.md](API-FR.md).
