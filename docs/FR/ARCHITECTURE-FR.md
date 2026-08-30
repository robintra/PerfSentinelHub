# Comment le Hub s'articule

Cinq schémas, chacun répondant à une question que le README énonce en prose mais ne
dessine pas. Les sources vivent dans [`../diagrams/mmd/`](../diagrams/mmd), les SVG
rendus dans [`../diagrams/svg/`](../diagrams/svg). Modifier un schéma veut dire modifier
le `.mmd` et réexporter les deux thèmes, jamais retoucher le SVG à la main.

## L'ensemble d'un seul coup d'œil

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/diagrams/svg/hub-integration_dark.svg">
  <img alt="Comment le Hub, la flotte, le navigateur et le moteur s'articulent" src="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/diagrams/svg/hub-integration.svg">
</picture>

Un seul Hub sert deux publics qui ne se recouvrent jamais. Le navigateur reçoit le
lanceur et rien d'autre. Un greffon d'IDE ou un job de CI reçoit `/api/findings` et
n'ouvre jamais un écran. Ce partage n'est imposé par aucune authentification, il est
simplement ce que chaque client demande, et il vaut mieux le savoir avant de lire l'une
ou l'autre surface.

Le moteur apparaît deux fois sur ce tableau et c'est le même binaire les deux fois :
une fois comme sous-processus que le Hub lance pour produire un rapport, une fois comme
daemon dont il collecte. Rien du Hub ne réside dans le moteur, et rien du moteur ne
réside dans le Hub.

## Push et poll, et lequel des deux possède la joignabilité

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/diagrams/svg/push-and-poll_dark.svg">
  <img alt="Le chemin de push, le chemin de poll, et le fait qu'un seul possède" src="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/diagrams/svg/push-and-poll.svg">
</picture>

Les deux chemins écrivent des findings. Un seul écrit la joignabilité.

Un daemon qui pousse avec succès prouve qu'il peut joindre le Hub. Il ne prouve rien
sur la capacité du Hub à le joindre, qui est une autre route à travers un autre jeu de
pare-feux, et c'est cette direction dont un opérateur a besoin quand une source se tait.
Le gestionnaire d'import ne touche donc jamais à `source_state` : une source dont le
push arrive alors que son poll échoue continue de rapporter `unreachable_since`, et
c'est juste plutôt que périmé.

Les deux chemins diffèrent aussi de contre-pression, délibérément. Le poll bloque sur le
verrou d'écriture parce qu'il est le travail programmé du Hub lui-même et qu'il peut
attendre. Un import abandonne au bout de cinq secondes avec `503 Retry-After: 1`, parce
qu'un téléverseur lent ne doit pas pouvoir bloquer la collecte de toute la flotte.

## Ce que fait réellement une analyse lancée

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/diagrams/svg/analysis-run_dark.svg">
  <img alt="Un run, de la soumission au rapport embarqué" src="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/diagrams/svg/analysis-run.svg">
</picture>

Deux choses de cette séquence se manquent facilement.

La première est que la validation a lieu **avant** la mise en file. Une combinaison
impossible, une fenêtre de trois heures contre un daemon qui en garde dix minutes, est
refusée pendant que l'opérateur regarde encore le formulaire, et non découverte trois
minutes plus tard sous la forme d'un run échoué.

La seconde est qu'un run vaut deux lancements du moteur, et non un. Les sous-commandes
de query émettent du texte, du JSON ou du SARIF, et seule `report` écrit du HTML, donc
la source est lue vers un JSON de rapport, puis ce JSON est rendu. Une source daemon
saute entièrement le premier lancement, puisque son propre `/api/export/report` renvoie
déjà exactement ce JSON.

## Trois horloges de rétention

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/diagrams/svg/retention-clocks_dark.svg">
  <img alt="Findings, fenêtre de statut et rapports rendus expirent chacun sur son horloge" src="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/diagrams/svg/retention-clocks.svg">
</picture>

Aucune des trois ne se déduit des autres, et elles sont appliquées par trois mécanismes
différents.

Les findings expirent sur un worker qui tourne une fois par jour et purge par tranches,
de sorte qu'une purge longue ne puisse pas rejeter les imports pendant toute sa durée.
La fenêtre de statut n'est pas un worker du tout, c'est un `CASE` évalué à la lecture,
ce qui explique que le statut d'un finding puisse changer sans que rien n'ait été écrit.
Les rapports rendus expirent sur un balayage qui tourne toutes les soixante secondes,
plus fin que la durée qu'il applique, pour que le compte à rebours que voit un lecteur
ne survive jamais au fichier qu'il décompte.

Le piège que ce schéma existe pour éviter : un poll qui omet un finding ne le résout
pas. Le tampon circulaire du daemon peut simplement l'avoir évincé, et absent n'est pas
la même chose que résolu. Seule la rétention retire une ligne.

## Un run atteint toujours un état terminal

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/diagrams/svg/run-states_dark.svg">
  <img alt="Les états que traverse un run et qui déplace chaque arête" src="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/diagrams/svg/run-states.svg">
</picture>

Quoi qu'il arrive, une ligne ne reste pas en `running`. Un process qui meurt en cours de
run laisse une ligne orpheline, et le démarrage suivant la marque `interrupted` plutôt
que de la laisser à l'état de question.

Rien n'est rejoué. Une reprise silencieuse lancerait une seconde requête lourde vers un
backend que personne n'a demandé d'interroger deux fois, donc un run interrompu attend
qu'un humain décide. `expired` n'est pas un échec non plus : le rapport a été supprimé à
l'heure prévue et le run garde ses paramètres, donc il peut être relancé tel quel.

## Modifier un schéma

Les sources `.mmd` font foi. Elles ne portent aucun bloc de thème `%%{init}%%`, et c'est
volontaire : le thème est un argument d'export, ce qui permet à une source unique de
produire à la fois le SVG clair et le sombre.

L'export est manuel, par [mermaid.live](https://mermaid.live) : coller la source,
choisir le thème, utiliser le bouton d'export SVG. C'est ainsi qu'a été produite la
famille perf-sentinel et les deux concordent, ce qui compte puisque les SVG se côtoient
dans le même genre de document.

`mermaid-cli` n'en est pas un substitut direct. Mesuré sur les sources de ce dépôt,
`mmdc` sans option de fond écrit `hsl(80, 100%, 96.2%)` là où la famille existante a
`rgb(255, 255, 255)`, et `-t dark -b '#232030'` écrit malgré tout
`hsl(20, 1.6%, 12.4%)` plutôt que le `rgb(35, 32, 48)` de la famille, le fond propre au
thème l'emportant sur l'option. Il reste utile pour contrôler qu'une source parse, ce
qui vaut la peine avant d'ouvrir un navigateur :

```bash
npx -y @mermaid-js/mermaid-cli@latest -i docs/diagrams/mmd/<nom>.mmd -o /tmp/check.svg
```

Cette invocation exige un Chrome ou un Chromium que Puppeteer sache trouver, et le dit
clairement quand elle n'en trouve pas.

Les deux fichiers d'une paire doivent exister. Un schéma livré avec la seule variante
claire est une dalle blanche pour tout lecteur en thème sombre, ce qui est exactement la
dérive que la convention de nommage existe pour rendre visible.

[`../diagrams/HUB-INTEGRATION-SPEC.md`](../diagrams/HUB-INTEGRATION-SPEC.md) est un
document d'une autre nature : un cahier de dessin pour la version faite à la main du
premier schéma, avec la palette, la géométrie et le sens que porte chaque tireté.
