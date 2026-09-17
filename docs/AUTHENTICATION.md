# Authentication

The Hub can sign browser users in against an OAuth2 provider itself, so no
authenticating proxy has to stand in front of it. It is off by default, and off
the Hub authenticates nobody, as described in [LIMITATIONS.md](LIMITATIONS.md).

## What it covers

With `Hub:Auth:Enabled`, every route needs a session except the ones a machine
calls:

| Route                                                            | Access                                                                  |
|------------------------------------------------------------------|-------------------------------------------------------------------------|
| `/` and the launcher's files                                     | session, otherwise a redirect to the provider                           |
| `/api/status`, `/api/sources`, `/api/incidents`, `/api/analyses` | session, otherwise `401`                                                |
| `/reports/`                                                      | session, `401` in the launcher's frame, a redirect when opened directly |
| `/api/findings`                                                  | open, for IDE plugins and CI jobs                                       |
| `POST /api/import/findings`                                      | its own `X-API-Key`, as before                                          |
| `/health/live`, `/health/ready`, `/metrics`                      | open, for probes and the scrape                                         |

The API answers `401` rather than a redirect because a fetch cannot follow a
redirect to the provider's origin. The launcher reloads itself on a `401`, and
the reload is what sends the browser to sign in again.

The routes left open still serve whoever reaches the port: `/api/findings`
returns every finding of every source. Keep the network boundary for them, a
NetworkPolicy or an ingress that does not publish those paths.

Any user the provider signs in is let in. Restrict who may use the Hub on the
provider side, by assigning the client to a group or to users.

## How it works

The framework's own OAuth2 and cookie handlers, with no package added. The
browser goes through the authorization code flow with PKCE, the Hub exchanges
the code at the token endpoint, then calls the userinfo endpoint with the access
token and records the field named by `Hub:Auth:IdentityClaim` as the user. A
userinfo without that field fails the sign-in rather than opening a nameless
session. A failed sign-in, that one or a user cancelling on the provider's
screen, answers `403` with a line sending the user back to the Hub's home page,
and logs the reason as a warning (event `1900`). No token is kept: the session
is an encrypted `hub_session` cookie, `Secure`, `HttpOnly`, `SameSite=Lax`,
valid 8 hours and renewed while in use. There is no sign-out button.

The signed-in user is what the topbar shows and what a run records as
`requested_by`. `Hub:Analysis:IdentityHeader` is ignored for a signed-in user,
since any client can send that header.

The keys that encrypt the cookie live in a `keys` directory next to
`Hub:DatabasePath`, on the data volume, so a restart does not sign everyone out.
The Hub logs at startup that no XML encryptor is configured: the keys sit
unencrypted on the volume, which holds the database under the same trust.

TLS usually ends at the ingress. The Hub trusts `X-Forwarded-Proto` from any
peer so the redirect URI it sends reads `https`. Only the scheme is taken from
the forwarded headers.

## Settings

| Setting                            | Default                | Validation                                                         |
|------------------------------------|------------------------|--------------------------------------------------------------------|
| `Hub:Auth:Enabled`                 | `false`                | Nothing below is read or required while off                        |
| `Hub:Auth:AuthorizationEndpoint`   | none                   | Absolute HTTPS (HTTP on loopback only), no credentials or fragment |
| `Hub:Auth:TokenEndpoint`           | none                   | Same                                                               |
| `Hub:Auth:UserInformationEndpoint` | none                   | Same                                                               |
| `Hub:Auth:ClientId`                | none                   | Required                                                           |
| `Hub:Auth:ClientSecret`            | none                   | Required, supplied through a Secret                                |
| `Hub:Auth:Scopes`                  | `openid profile email` | Space-separated                                                    |
| `Hub:Auth:IdentityClaim`           | `email`                | The userinfo field recorded as the user                            |

Declare `https://<hub host>/auth/callback` as the client's redirect URI, and
make the client confidential.

Under Helm:

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

The secret reaches the pod as `Hub__Auth__ClientSecret`, never through the
values.

## Providers

The endpoints below come from each provider's documentation. The Hub's tests
play the provider with a fake one.

| Provider        | Authorization, token, userinfo                                                                                                            | Scopes                 | `IdentityClaim`      |
|-----------------|-------------------------------------------------------------------------------------------------------------------------------------------|------------------------|----------------------|
| Keycloak        | `https://<host>/realms/<realm>/protocol/openid-connect/auth`, `.../token`, `.../userinfo`                                                 | `openid profile email` | `preferred_username` |
| Entra ID        | `https://login.microsoftonline.com/<tenant>/oauth2/v2.0/authorize`, `.../oauth2/v2.0/token`, `https://graph.microsoft.com/oidc/userinfo`  | `openid profile email` | `email`              |
| Google          | `https://accounts.google.com/o/oauth2/v2/auth`, `https://oauth2.googleapis.com/token`, `https://openidconnect.googleapis.com/v1/userinfo` | `openid profile email` | `email`              |
| GitLab          | `https://<host>/oauth/authorize`, `https://<host>/oauth/token`, `https://<host>/oauth/userinfo`                                           | `openid profile email` | `preferred_username` |
| Bitbucket Cloud | `https://bitbucket.org/site/oauth2/authorize`, `https://bitbucket.org/site/oauth2/access_token`, `https://api.bitbucket.org/2.0/user`     | `account`              | `username`           |

Bitbucket is not an OpenID provider: its userinfo is the REST user resource,
and its scopes are the ones granted to the OAuth consumer.
