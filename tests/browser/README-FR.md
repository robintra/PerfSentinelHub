# Suite de démonstration navigateur

Capture les écrans du lanceur que montrent `docs/` et le README. Elle produit
des artefacts, pas des assertions : rien ici ne conditionne la CI, et
`make verify-fast` ne la lance pas.

```bash
npm install
npx playwright install chromium
npm run demo
```

Le résultat arrive dans `docs/img/hub/` : cinq écrans en `<nom>.png` (clair) et
`<nom>-dark.png` (sombre), plus `launcher_light.gif` et `launcher_dark.gif`.

## Ce que global-setup doit monter d'abord

Le Hub n'a ni semeur, ni chargeur de fixtures, ni mode démo. Sa validation
refuse de démarrer sans source, et la vue d'un daemon est lue en direct plutôt
que depuis le stockage. Une capture non vide exige donc un Hub qui tourne
vraiment contre des daemons qui répondent vraiment, alors `global-setup.ts`
monte :

- deux faux daemons (`demo/fake-daemon.js`) rejouant les captures de
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

## Deux choses qu'elle attend de l'extérieur

**Un binaire perf-sentinel.** Sans lui le Hub répond `503` à
`POST /api/analyses` et trois écrans sur cinq sont morts. La mise en place
cherche un dépôt `perf-sentinel` voisin avec une compilation release, ou prend
`HUB_ENGINE_BINARY`.

**Le SDK épinglé.** `global.json` épingle 10.0.400 en `rollForward: disable`,
qui n'est en général pas le `dotnet` du `PATH`. La mise en place place
`/usr/local/share/dotnet` devant quand il est présent.

`ffmpeg` n'est nécessaire que pour les GIF. `build-gif.sh` sort en erreur quand
il ne trouve aucun enregistrement, pour qu'une exécution sans effet ne puisse
pas passer pour un succès et livrer les fichiers de la fois précédente.
