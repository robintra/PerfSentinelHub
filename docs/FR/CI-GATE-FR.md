# Activation de la porte de CI requise

`CI / Gate` n'est digne de confiance que si sa source attendue est l'application GitHub dédiée. Un job GitHub Actions venant d'un fork peut choisir le même nom d'affichage, donc un contrôle de statut requis identifié par son seul nom n'est pas une frontière de sécurité.

Une fois le dépôt public :

1. Créez une application GitHub dédiée avec la seule permission `Checks: read and write` sur le dépôt, désactivez les webhooks, et ne l'installez que sur ce dépôt.
2. Stockez son App ID et sa clé privée comme secrets GitHub Actions nommés `CI_GATE_APP_ID` et `CI_GATE_APP_PRIVATE_KEY`.
3. Lancez une pull request interne de confiance, ou un `workflow_dispatch` sur un fork inspecté, pour que l'application publie un contrôle `CI / Gate`.
4. Protégez la branche par défaut avec le contrôle de statut requis `CI / Gate` et sélectionnez cette application GitHub dédiée comme source attendue. Ne sélectionnez jamais "any source" ni GitHub Actions.
5. Vérifiez avec un fork qui ajoute un job GitHub Actions toujours vert nommé `CI / Gate` : le contrôle GitHub Actions ne doit pas satisfaire l'exigence liée à l'application.

Le publieur demande un jeton d'installation de courte durée restreint à ce dépôt et à `checks: write`. Il n'y a aucun repli sur `GITHUB_TOKEN`. Tant que l'application n'est pas installée, que les deux secrets n'existent pas et que le contrôle requis n'est pas lié à l'application, la porte du dépôt est volontairement considérée comme inactive.

GitHub documente qu'une exécution utilise la version du workflow à la SHA ou à la ref de son événement, et qu'un contrôle de statut requis peut sélectionner une application GitHub précise comme source attendue :

- <https://docs.github.com/en/actions/concepts/workflows-and-actions/workflows>
- <https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/about-protected-branches>
