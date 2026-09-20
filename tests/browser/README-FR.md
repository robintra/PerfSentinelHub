# Suite de démonstration navigateur

Capture les écrans du lanceur que montrent `docs/` et le README. Elle produit
des artefacts, pas des assertions : rien ici ne conditionne la CI, et
`make verify-fast` ne la lance pas.

```bash
npm install
npx playwright install chromium
npm run demo
```

Le résultat arrive dans `docs/img/hub/` : sept écrans en `<nom>.png` (clair) et
`<nom>-dark.png` (sombre), plus `launcher-handoff.png`, New analysis tel qu'un lien
d'incident le remplit, et `launcher_light.gif` et `launcher_dark.gif`. Le septième
est `launcher-ack.png`, la page d'acquittement ouverte comme un lien Grafana
l'ouvre. Les GIF parcourent les quatre écrans à onglet, plus l'analyse et le
rapport ouverts depuis eux, et se terminent sur le passage à New analysis. La
page d'acquittement reste hors de la visite : elle s'atteint par un lien depuis
l'extérieur du lanceur, et le lien Ack du tableau des incidents n'a aucun
finding où atterrir dans ces fixtures, parce que le daemon des incidents est
semé avec ses propres endpoints alors que les findings de la flotte viennent du
fichier de traces de démonstration du moteur.

## Ce que global-setup doit monter d'abord

Le Hub n'a ni semeur, ni chargeur de fixtures, ni mode démo. Sa validation
refuse de démarrer sans source, et la vue d'un daemon est lue en direct plutôt
que depuis le stockage. Une capture non vide exige donc un Hub qui tourne
vraiment contre des daemons qui répondent vraiment, alors `global-setup.ts`
monte :

- quatre faux daemons (`demo/fake-daemon.js`) rejouant les captures de
  `demo/fixtures/`,
- le Hub, construit depuis ce dépôt et lancé depuis son propre binaire,
- quatre analyses soumises par l'API, choisies pour les états où elles
  finissent : deux réussissent, une tombe sur une source injoignable, une est
  refusée avant d'être mise en file parce que la fenêtre dépasse ce que le
  backend conserve.

## Les fixtures sont des captures, pas des inventions

`demo/fixtures/` contient ce qu'un vrai daemon a répondu. `daemon-config.json`
est son `api/config` mot pour mot. Les findings viennent de
`perf-sentinel analyze --format json` sur le fichier de traces de démonstration
du moteur, donc chaque détecteur, chaque sévérité et chaque service des captures
est bien un que le moteur a réellement produit.

`demo/capture-fixtures.sh <moteur>` rafraîchit l'ensemble contre un daemon
vivant. À lancer quand la version du moteur bouge, pour que les captures
continuent de montrer ce que le moteur répond aujourd'hui.

Seules les valeurs de jauges de `daemon-status-*.json` sont choisies : un
daemon au repos rapporte des zéros, et une capture de zéros n'apprend rien. Un
daemon est proche de son plafond pour que la coloration se voie, l'autre est à
l'aise.

Les acquittements sont capturés de la même façon, depuis deux exécutions de
daemon supplémentaires, parce qu'un seul daemon ne peut pas porter les deux
sortes d'acquittement sur une même signature : il refuse un acquittement à lui
sur une signature que sa base CI porte déjà. Une exécution prend l'acquittement
à chaud, l'autre charge une base qui le porte, et les trois listes qui en
sortent donnent à la page d'acquittement une ligne par cas sur un seul finding :
un daemon qui relaie et ne porte aucun acquittement, un qui porte le sien, un
dont la base CI le porte. La quatrième ligne est le daemon sans identifiant
d'acquittement, ce qui relève de la configuration et non d'une capture.

Seul l'acquittement posé sur un finding que la page n'affiche pas porte une
expiration. Le Hub sert un acquittement miroir tant que son expiration est
devant et l'abandonne ensuite, donc une date posée sur le finding de la page
viderait cette ligne quelques mois après la capture et l'écran se lirait comme
si personne n'avait jamais acquitté quoi que ce soit.

Les horodatages du finding lui-même relèvent de la même règle, en sens inverse.
`first_seen_ms` est figé dans `daemon-findings.json`, alors que le Hub date le
`last_seen` d'un finding à sa propre horloge, donc le faux daemon glisse la
liste des findings jusqu'au présent au moment de la servir. Sans cela, la page
d'acquittement afficherait un finding vu pour la première fois un an avant sa
dernière observation.

Les incidents sont capturés de la même façon, depuis un daemon nourri de cinq
livraisons Alertmanager portant trois labels `namespace` et une alerte sans, pour
que la colonne montre une valeur et la cellule vide, et le faux daemon glisse
leurs horodatages jusqu'au présent au moment de les servir. L'écran affiche chaque instant comme une
durée, donc un corps capturé le trimestre dernier mettrait "il y a 3 mois" sur
un OOM kill et se lirait comme un écran cassé. Un seul décalage sur chaque
champ en millisecondes, pour que les fenêtres, les findings figés et la lecture
avant ou après le redémarrage gardent les distances mesurées par le daemon.

## Deux choses qu'elle attend de l'extérieur

**Un binaire perf-sentinel.** Sans lui le Hub répond `503` à
`POST /api/analyses` et trois écrans sur sept sont morts. La mise en place
cherche un dépôt `perf-sentinel` voisin avec une compilation release, ou prend
`HUB_ENGINE_BINARY`.

**Le SDK épinglé.** `global.json` épingle 10.0.401 en `rollForward: disable`,
qui n'est en général pas le `dotnet` du `PATH`. La mise en place appelle le
`dotnet` épinglé par son nom, `$DOTNET_ROOT/dotnet` quand la variable est
posée et `/usr/local/share/dotnet/dotnet` sinon, plutôt que de placer l'un de
ces répertoires devant le `PATH` : l'ordre de recherche décide mal quelle
chaîne d'outils construit ce qu'on s'apprête à photographier. Posez
`DOTNET_ROOT` quand l'installation système est restée en retrait de
l'épinglage.

`ffmpeg` n'est nécessaire que pour les GIF. `build-gif.sh` sort en erreur quand
il ne trouve aucun enregistrement, pour qu'une exécution sans effet ne puisse
pas passer pour un succès et livrer les fichiers de la fois précédente.
