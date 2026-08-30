<p align="center">
    <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fraw.githubusercontent.com%2Frobintra%2FPerfSentinelHub%2Fmain%2Fglobal.json&query=%24.sdk.version&label=.NET&color=512BD4&logo=dotnet&logoColor=white" alt=".NET" /></a>
    <a href="https://github.com/robintra/PerfSentinelHub/actions/workflows/ci.yml"><img src="https://github.com/robintra/PerfSentinelHub/actions/workflows/ci.yml/badge.svg" alt="CI" /></a>
    <a href="https://github.com/robintra/PerfSentinelHub/actions/workflows/security-audit.yml"><img src="https://github.com/robintra/PerfSentinelHub/actions/workflows/security-audit.yml/badge.svg" alt="Security Audit" /></a>
    <a href="https://github.com/robintra/PerfSentinelHub/actions/workflows/codeql.yml"><img src="https://github.com/robintra/PerfSentinelHub/actions/workflows/codeql.yml/badge.svg" alt="CodeQL" /></a>
    <a href="https://sonarcloud.io/summary/overall?id=robintrassard_PerfSentinelHub"><img src="https://sonarcloud.io/api/project_badges/measure?project=robintrassard_PerfSentinelHub&metric=coverage" alt="Coverage" /></a>
    <a href="https://sonarcloud.io/summary/overall?id=robintrassard_PerfSentinelHub"><img src="https://sonarcloud.io/api/project_badges/measure?project=robintrassard_PerfSentinelHub&metric=alert_status" alt="Quality Gate" /></a>
    <a href="https://github.com/robintra/PerfSentinelHub/actions/workflows/release.yml"><img src="https://github.com/robintra/PerfSentinelHub/actions/workflows/release.yml/badge.svg" alt="Release" /></a>
</p>

# PerfSentinelHub

**Un point d'accès durable et unique pour les findings que produisent vos daemons
[perf-sentinel](https://github.com/robintra/perf-sentinel), et une interface de navigateur
qui lance une analyse sans passer par un terminal.** Un service NativeAOT adossé à SQLite.
Le push depuis le daemon est le chemin primaire, le poll est un filet de sécurité, et les
enveloppes de findings restent compatibles en lecture pendant 180 jours par défaut.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/diagrams/svg/hub-integration_dark.svg">
  <img alt="Comment le Hub, la flotte, le navigateur et le moteur s'articulent" src="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/diagrams/svg/hub-integration.svg">
</picture>

## Démarrer en local en cinq minutes

Prérequis : le SDK .NET 10.0.400 et un daemon perf-sentinel joignable.

```bash
Hub__DatabasePath=/tmp/perf-sentinel-hub.db \
Hub__Sources__0__Id=local \
Hub__Sources__0__Name='Local daemon' \
Hub__Sources__0__Environment=development \
Hub__Sources__0__BaseUrl=http://localhost:4318 \
Hub__Sources__0__ImportApiKey="$(openssl rand -hex 16)" \
ASPNETCORE_URLS=http://localhost:5080 \
dotnet run --project PerfSentinelHub

curl http://localhost:5080/health/ready
curl http://localhost:5080/api/findings
```

Le premier poll démarre immédiatement. Le lanceur est sur `http://localhost:5080/`, et le
fichier SQLite survit aux redémarrages au chemin configuré.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/img/hub/launcher-new-dark.png">
  <img alt="Lancement d'une analyse contre un backend de traces" src="https://raw.githubusercontent.com/robintra/PerfSentinelHub/main/docs/img/hub/launcher-new.png">
</picture>

## Installer avec Helm

Le chart source déploie un réplica et un volume persistant. Fournissez au moins une source
et un digest d'image immuable :

```bash
helm upgrade --install perf-sentinel-hub deploy/helm/perf-sentinel-hub \
  --set image.repository=ghcr.io/robintra/perf-sentinel-hub \
  --set image.digest=sha256:IMAGE_DIGEST \
  --set 'sources[0].id=production' \
  --set 'sources[0].name=Production' \
  --set 'sources[0].environment=production' \
  --set 'sources[0].baseUrl=http://perf-sentinel.observability:4318' \
  --set persistence.size=5Gi
```

Les identifiants ne vont jamais dans les values Helm. Pour un poll authentifié, mettez la
valeur dans un Secret et réglez `sources[].authHeaderName`, `authSecretName` et
`authSecretKey`. Pour le push depuis le daemon, réglez `sources[].importSecretName` et
`importSecretKey`, avec au moins 32 caractères.

Pour une release publique, installez par digest et non par tag. Un tag de version est un
indice de découverte, jamais une identité de déploiement :

```bash
IMAGE_DIGEST="$(jq -r .image.digest release/release-manifest.json)"
docker pull "ghcr.io/robintra/perf-sentinel-hub@$IMAGE_DIGEST"

CHART=ghcr.io/robintra/charts/perf-sentinel-hub
CHART_DIGEST="$(oras resolve "$CHART:0.1.0")"
helm pull "oci://$CHART@$CHART_DIGEST"
```

Ces commandes de registre ne fonctionnent qu'une fois la répétition publique et la
publication réussies.

## Documentation

| Document                                                   | Contenu                                                                                                          |
|------------------------------------------------------------|------------------------------------------------------------------------------------------------------------------|
| [docs/FR/ARCHITECTURE-FR.md](docs/FR/ARCHITECTURE-FR.md)   | Cinq schémas : la topologie, push contre poll, ce que fait un run, les horloges de rétention, les états d'un run |
| [docs/FR/CONFIGURATION-FR.md](docs/FR/CONFIGURATION-FR.md) | Chaque réglage, où le Hub se connecte, et une source https derrière une CA privée                                |
| [docs/FR/API-FR.md](docs/FR/API-FR.md)                     | Les API d'import, de lecture et d'analyse                                                                        |
| [docs/FR/LAUNCHER-FR.md](docs/FR/LAUNCHER-FR.md)           | L'interface de navigateur, ses commandes imprimées et ses rapports live                                          |
| [docs/FR/OPERATIONS-FR.md](docs/FR/OPERATIONS-FR.md)       | Fraîcheur, rétablissement, sauvegarde et restauration                                                            |
| [RELEASING.md](RELEASING.md)                               | Ce que contient une release, comment elle est signée, et comment en vérifier une publiquement                    |
| [CONTRIBUTING.md](CONTRIBUTING.md)                         | Les portes locales, la chaîne d'outils épinglée, et les règles de pull request                                   |

## Ce que ce n'est pas

Ni ingress, ni authentification d'utilisateur, ni import CI ou SARIF, ni écrivain
d'acquittements, ni sauvegarde distante. La commande `backup` locale prend un instantané
de la base, mais expédier ce fichier hors du cluster reste le travail de l'opérateur.

L'exposition réseau et l'authentification relèvent de la prochaine conception
indépendante. Les acquittements restent dans le dépôt que consomme perf-sentinel.

Chaque badge ci-dessus rapporte quelque chose d'observé. Ceux de l'image de conteneur et
du chart Helm sont délibérément absents jusqu'à ce qu'une première release publie leurs
pages de paquet, parce qu'un badge qui ne mène nulle part est une promesse plutôt qu'une
preuve.

## Licence

[GNU Affero General Public License v3.0](LICENSE). Les applications et les greffons d'IDE
communiquent avec le Hub par HTTP plutôt qu'en le liant. Si vous modifiez le Hub et offrez
cette version modifiée sur un réseau, la section 13 de l'AGPL s'applique. Ceci est un
résumé pratique et non un avis juridique.
