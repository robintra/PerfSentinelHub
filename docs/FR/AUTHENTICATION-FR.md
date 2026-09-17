# Authentification

Le Hub peut connecter lui-même les utilisateurs du navigateur auprès d'un
fournisseur OAuth2, si bien qu'aucun proxy authentifiant n'a besoin de se tenir
devant lui. C'est désactivé par défaut, et désactivé le Hub n'authentifie
personne, comme le décrit [LIMITATIONS-FR.md](LIMITATIONS-FR.md).

## Ce que ça couvre

Avec `Hub:Auth:Enabled`, chaque route exige une session, sauf celles qu'appelle
une machine :

| Route                                                            | Accès                                                                      |
|------------------------------------------------------------------|----------------------------------------------------------------------------|
| `/` et les fichiers du lanceur                                   | session, sinon redirection vers le fournisseur                             |
| `/api/status`, `/api/sources`, `/api/incidents`, `/api/analyses` | session, sinon `401`                                                       |
| `/reports/`                                                      | session, `401` dans le cadre du lanceur, redirection si ouvert directement |
| `/api/findings`                                                  | ouvert, pour les plugins IDE et les jobs CI                                |
| `POST /api/import/findings`                                      | sa propre `X-API-Key`, comme avant                                         |
| `/health/live`, `/health/ready`, `/metrics`                      | ouvert, pour les sondes et le scrape                                       |

L'API répond `401` plutôt qu'une redirection, car un fetch ne peut pas suivre
une redirection vers l'origine du fournisseur. Le lanceur se recharge sur un
`401`, et c'est ce rechargement qui renvoie le navigateur se connecter.

Les routes laissées ouvertes servent toujours qui joint le port :
`/api/findings` renvoie tous les findings de toutes les sources. Gardez pour
elles la frontière réseau, une NetworkPolicy ou un ingress qui ne publie pas ces
chemins.

Tout utilisateur que le fournisseur connecte entre. Restreignez qui peut
utiliser le Hub côté fournisseur, en assignant le client à un groupe ou à des
utilisateurs.

## Comment ça marche

Les handlers OAuth2 et cookie du framework lui-même, sans package ajouté. Le
navigateur passe par le flux authorization code avec PKCE, le Hub échange le
code sur le token endpoint, puis appelle l'endpoint userinfo avec l'access token
et enregistre comme utilisateur le champ nommé par `Hub:Auth:IdentityClaim`. Un
userinfo sans ce champ fait échouer la connexion plutôt que d'ouvrir une session
sans nom. Aucun token n'est conservé : la session est un cookie chiffré
`hub_session`, `Secure`, `HttpOnly`, `SameSite=Lax`, valable 8 heures et
prolongé tant qu'il sert. Il n'y a pas de bouton de déconnexion.

L'utilisateur connecté est ce qu'affiche la barre du haut et ce qu'un run
enregistre comme `requested_by`. `Hub:Analysis:IdentityHeader` est ignoré pour
un utilisateur connecté, puisque n'importe quel client peut envoyer cet en-tête.

Les clés qui chiffrent le cookie vivent dans un répertoire `keys` à côté de
`Hub:DatabasePath`, sur le volume de données, si bien qu'un redémarrage ne
déconnecte personne. Le Hub journalise au démarrage qu'aucun chiffreur XML n'est
configuré : les clés reposent en clair sur le volume, qui porte la base sous la
même confiance.

TLS s'arrête en général à l'ingress. Le Hub fait confiance à
`X-Forwarded-Proto` venant de n'importe quel pair, pour que l'URI de redirection
qu'il envoie soit en `https`. Seul le schéma est pris des en-têtes transmis.

## Réglages

| Réglage                            | Défaut                 | Validation                                                               |
|------------------------------------|------------------------|--------------------------------------------------------------------------|
| `Hub:Auth:Enabled`                 | `false`                | Rien de ce qui suit n'est lu ni exigé tant que c'est désactivé           |
| `Hub:Auth:AuthorizationEndpoint`   | aucun                  | HTTPS absolu (HTTP en loopback seulement), sans identifiants ni fragment |
| `Hub:Auth:TokenEndpoint`           | aucun                  | Idem                                                                     |
| `Hub:Auth:UserInformationEndpoint` | aucun                  | Idem                                                                     |
| `Hub:Auth:ClientId`                | aucun                  | Obligatoire                                                              |
| `Hub:Auth:ClientSecret`            | aucun                  | Obligatoire, fourni par un Secret                                        |
| `Hub:Auth:Scopes`                  | `openid profile email` | Séparés par des espaces                                                  |
| `Hub:Auth:IdentityClaim`           | `email`                | Le champ userinfo enregistré comme utilisateur                           |

Déclarez `https://<hôte du hub>/auth/callback` comme URI de redirection du
client, et faites du client un client confidentiel.

Sous Helm :

```yaml
hub:
  auth:
    enabled: true
    authorizationEndpoint: https://sso.example.com/realms/acme/protocol/openid-connect/auth
    tokenEndpoint: https://sso.example.com/realms/acme/protocol/openid-connect/token
    userInformationEndpoint: https://sso.example.com/realms/acme/protocol/openid-connect/userinfo
    clientId: perf-sentinel-hub
    clientSecretName: perf-sentinel-hub-oauth
    clientSecretKey: client-secret
    identityClaim: preferred_username
```

Le secret atteint le pod comme `Hub__Auth__ClientSecret`, jamais par les values.

## Fournisseurs

Les endpoints ci-dessous viennent de la documentation de chaque fournisseur. Les
tests du Hub jouent le fournisseur avec un faux.

| Fournisseur     | Autorisation, token, userinfo                                                                                                             | Scopes                 | `IdentityClaim`      |
|-----------------|-------------------------------------------------------------------------------------------------------------------------------------------|------------------------|----------------------|
| Keycloak        | `https://<hôte>/realms/<realm>/protocol/openid-connect/auth`, `.../token`, `.../userinfo`                                                 | `openid profile email` | `preferred_username` |
| Entra ID        | `https://login.microsoftonline.com/<tenant>/oauth2/v2.0/authorize`, `.../oauth2/v2.0/token`, `https://graph.microsoft.com/oidc/userinfo`  | `openid profile email` | `email`              |
| Google          | `https://accounts.google.com/o/oauth2/v2/auth`, `https://oauth2.googleapis.com/token`, `https://openidconnect.googleapis.com/v1/userinfo` | `openid profile email` | `email`              |
| GitLab          | `https://<hôte>/oauth/authorize`, `https://<hôte>/oauth/token`, `https://<hôte>/oauth/userinfo`                                           | `openid profile email` | `preferred_username` |
| Bitbucket Cloud | `https://bitbucket.org/site/oauth2/authorize`, `https://bitbucket.org/site/oauth2/access_token`, `https://api.bitbucket.org/2.0/user`     | `account`              | `username`           |

Bitbucket n'est pas un fournisseur OpenID : son userinfo est la ressource REST de
l'utilisateur, et ses scopes sont ceux accordés au consumer OAuth.
