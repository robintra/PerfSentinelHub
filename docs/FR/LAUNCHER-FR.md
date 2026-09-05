# Le lanceur

Le Hub sert une interface de navigateur sur `/`, depuis la même origine que les rapports
qu'elle ouvre. HTML, CSS et JavaScript simples. Sans framework, sans étape de build et
sans requête réseau : les deux polices sont en base64 dans `wwwroot/fonts.css` et chaque
icône est un SVG en ligne.

Cinq écrans : démarrer une analyse, suivre un run, lister les runs récents, lire la santé
de la flotte, lire les incidents enregistrés par les daemons.

## Le formulaire suit la source

Le formulaire s'adapte au `kind` de la source sélectionnée plutôt que d'offrir un
sélecteur indépendant entre live et historique. Un tel sélecteur laisserait un opérateur
composer des états impossibles, comme une fenêtre de trois heures contre un daemon qui en
garde dix minutes.

Un lien depuis l'écran des incidents arrive ici avec le formulaire rempli : le service de
l'incident, et sa fenêtre en plage absolue dont la fin est retenue à maintenant. Un bandeau
au-dessus des paramètres nomme l'incident, sous la forme `ns/service` quand il portait un
namespace, et dit qu'une analyse prend un service et aucun namespace, donc qu'elle couvre
ce service dans chacun d'eux. La source ne fait pas partie du lien, puisqu'un
daemon ne prend aucune fenêtre : sous un daemon le bandeau le dit et demande un backend de
traces.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/img/hub/launcher-sources-dark.png">
  <img alt="L'écran de santé de la flotte avec une ligne daemon dépliée" src="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/img/hub/launcher-sources.png">
</picture>

## Les jauges

Une jauge est teintée dès qu'elle approche d'un plafond qu'elle a publié. Rouge à partir
de 90 %, la ligne du conseiller du moteur lui-même et celle qui fait passer le verdict
d'une ligne à "près du plafond". Ambre à partir de 75 %, le palier propre au Hub, un cran
avant.

Chaque lecture montre ce qui a bougé depuis la précédente, montant depuis le chiffre
auquel cela se rapporte puis s'effaçant : rouge pour une hausse, vert pour une baisse.
Chacun de ces chiffres compte vers un plafond, donc la hausse est la direction qui coûte
quelque chose. L'uptime n'est ni teinté ni suivi, n'ayant pas de plafond et une seule
direction possible.

## Ce que le navigateur retient

Les replis qu'un lecteur a ouverts, dans le `localStorage` de ce navigateur, sous une
seule clé et comme replis ouverts seulement. Une ligne, son bloc terminal, ses réglages et
les groupes qu'ils contiennent reviennent tels qu'ils ont été laissés, et une ligne
laissée ouverte relit son daemon à la visite suivante sans qu'on la clique.

Rien d'autre que ces noms n'est stocké. Un navigateur qui refuse le stockage démarre
simplement tout replié.

Le thème est à trois positions : système, clair, sombre. Seul le clair ou le sombre résolu
atteint le DOM, de sorte que les feuilles de style voient deux valeurs et jamais trois. La
position est stockée sous `perf-sentinel:theme` dans `localStorage` et dans
`sessionStorage`, le second parce que le dashboard rendu lit cette clé exacte depuis cette
origine. C'est ce passage de relais qui impose que le lanceur et les rapports partagent
une origine.

## Les commandes imprimées

Chaque commande imprimée porte un onglet par shell, parce que la différence n'est pas
cosmétique. Un shell POSIX continue une ligne par une barre oblique inverse et échappe une
apostrophe en fermant puis rouvrant. PowerShell continue par un accent grave et double
l'apostrophe, et son ensemble de mots nus est plus étroit puisque la virgule y est
l'opérateur de tableau.

L'onglet qu'ouvre une première visite suit la plateforme, Windows recevant PowerShell. Le
choix propre du lecteur est retenu ensuite et s'applique d'un coup à toutes les commandes
de la page.

Aucune des deux commandes ne porte de valeur d'exemple. Le point d'accès est le `BaseUrl`
configuré de la source, et la commande du monitor porte l'intervalle de relecture choisi
sur cette ligne, de sorte qu'une ligne copiée est exécutable telle quelle et ne contredit
pas l'écran d'où elle vient. La seule chose qu'un opérateur tape encore est le nom de
service, qui lui appartient et qui est montré vide plutôt que deviné.

Les deux commandes disent où obtenir le moteur, puisque ni l'une ni l'autre ne passe par
le Hub. La note lie la release de la version exacte que ce Hub fait tourner, celle pour
laquelle les flags sont orthographiés. Sans version sondée, le lien retombe sur la liste
des releases plutôt que d'inventer un tag.

### Le run en ligne de commande

Le lanceur imprime le run sous forme de ligne de commande du moteur, pour qu'un opérateur
puisse l'emporter dans un terminal. Elle est bâtie depuis l'objet même que le formulaire
poste, jamais depuis le formulaire, de sorte que la commande imprimée et le run soumis ne
peuvent pas diverger.

C'est une commande et non les deux que le Hub lance : la sortie JSON et l'étape de rendu
existent pour que le Hub puisse bâtir un dashboard, et un terminal n'a besoin ni de l'une
ni de l'autre.

Les valeurs sont protégées pour un shell POSIX par des apostrophes simples, la seule forme
qui tienne pour un nom de service portant un `$` ou une apostrophe. Une source
authentifiée imprime `--auth-header-env` plutôt que son jeton, que le Hub détient et ne
divulgue jamais.

Les surcharges de détection n'ont pas de flag en ligne de commande, donc un run qui en a
changé une porte `-c perf-sentinel.toml`, et le fichier est imprimé à côté de la commande,
prêt à copier ou à télécharger. Le nom est sans point parce que le moteur ne découvre que
le `.perf-sentinel.toml` pointé, qu'un téléchargement peut ne pas préserver, donc la
commande nomme le fichier au lieu de s'en remettre à cette découverte.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/img/hub/launcher-report-dark.png">
  <img alt="Un rapport rendu, ouvert dans le lanceur" src="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/img/hub/launcher-report.png">
</picture>

## Les rapports live

Un rapport rendu depuis une source daemon devient live quand le `BaseUrl` de ce daemon est
une origine nue. Le rendu passe `--daemon-url`, et les contrôles de rafraîchissement et
d'acquittement du dashboard parlent alors à ce daemon depuis le navigateur du lecteur.

Deux conditions se trouvent hors du Hub : le `[daemon.cors] allowed_origins` du daemon
doit porter l'origine depuis laquelle ce Hub sert ses rapports, et le lecteur doit pouvoir
joindre le daemon directement.

Un daemon derrière un ingress à préfixe de chemin reçoit un rapport statique, parce que le
flag du moteur prend une origine et rien d'autre. Il en va de même de toute source daemon
quand le binaire configuré ne prend pas `--daemon-url` du tout : le moteur le déclare à
l'intérieur de sa feature `daemon`, donc un binaire bâti sans elle refuse l'argument au
lieu de l'ignorer, et un run qui le passerait ne rendrait rien. Le Hub interroge le
binaire une fois au démarrage, par `report --help`, et rend en statique quand la réponse
est non ou illisible.

Les liens de rapport se partagent à portée réseau du Hub et meurent avec la fenêtre de
rétention.

## La ligne de santé de la flotte

La ligne d'un daemon se déplie sur les jauges qu'il rapporte face à leurs plafonds et les
conseils qu'il écrit sur son propre tuning, dont `/metrics` ne porte ni les uns ni les
autres.

La ligne relit à un intervalle que le lecteur choisit, le même réglage que porte
`query monitor --refresh` plus une position éteinte. Une lecture ne remplace que les
jauges et les conseils : les réglages ne changent pas sans redémarrage, donc les
reconstruire jetterait des groupes ouverts pour rien. Replier la ligne arrête les
lectures.

Les réglages eux-mêmes sont un clic plus loin, groupés et repliés, chaque groupe montrant
combien de ses valeurs s'écartent des défauts du moteur. La ligne se termine par la
commande `perf-sentinel query monitor` pour la même vue dans un terminal.

## L'écran des incidents

Chaque ligne est un incident qu'un daemon a enregistré quand l'alerte de l'exploitant le
lui a posté, dans l'ordre où le moniteur du daemon imprime ses colonnes : début, namespace,
service, kind, fin, findings, capture, source. Le namespace est le label d'alerte que le
daemon a lu à côté du service, vide quand l'alerte n'en portait pas. Le daemon en est
l'auteur et le Hub copie son enregistrement, donc l'écran lit ce que le daemon a gelé et ne
redérive rien.

Ouvrir l'écran lit chaque daemon avant de dessiner, donc les lignes sont ce que la flotte
tient maintenant et non ce que le dernier poll a laissé : un exploitant alerté d'un OOM
kill attendrait sinon un poll horaire devant un tableau vide. Un bouton répète cette
lecture à la demande et s'éteint tant qu'une lecture est en vol. Aucun des deux ne peut
prendre la flotte d'assaut, un daemon lu il y a moins de dix secondes étant sauté et sa
copie stockée servie à la place.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/img/hub/launcher-incidents-dark.png">
  <img alt="L'écran des incidents avec une ligne dépliée sur ses findings figés" src="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/img/hub/launcher-incidents.png">
</picture>

Une ligne se déplie sur les findings que le daemon a gelés pour cet incident, chacun placé
avant l'incident ou après le redémarrage d'après son propre horodatage face à celui de
l'incident. La colonne capture porte la lecture du daemon de jusqu'où son anneau
remontait encore : complete, partial ou empty. Les durées sont relatives, jamais des
dates, et l'horodatage exact est dans l'infobulle. Cinq sélecteurs restreignent la liste,
par kind, service, namespace, environnement et daemon, celui du namespace apparaissant dès
qu'une ligne en a porté un. C'est le Hub qui les applique, donc les lignes plus anciennes
qu'un bouton charge ensuite, par centaine et depuis le magasin plutôt que depuis les
daemons, suivent le même filtre. Sous le tableau, une ligne par daemon dit quand sa copie a
été lue, ou qu'elle ne l'a jamais été, pour qu'une flotte calme ne se lise jamais comme une
copie périmée. Un daemon qui a refusé la clé du Hub sur cette route reçoit un bandeau
nommant le réglage à corriger, et ses findings restent collectés.

Une ligne dépliée porte un bouton Analyse this window sous la note qui décrit la fenêtre,
qui ouvre New
analysis sur le service et la fenêtre de l'incident, décrit sous Le formulaire suit la
source. La source reste au choix de l'exploitant, parce qu'un daemon ne prend aucune
fenêtre et que le daemon de l'incident lui-même analyserait son instantané en mémoire
plutôt que la fenêtre. Le lien garde ses paramètres, donc il se partage et se recharge, et
l'onglet New lui-même n'en porte aucun.

## Sûreté

Rien de ce qui vient du serveur n'est jamais écrit avec `innerHTML`. Chaque chaîne
affichée est un nœud de texte.
