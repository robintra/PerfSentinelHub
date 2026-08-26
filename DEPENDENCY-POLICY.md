# Dependency update policy

Dependabot is the only dependency update service for this repository. It owns NuGet packages,
Docker images, Helm chart dependencies, and GitHub Actions. Renovate and dependency auto-merge are
not allowed.

## Ordinary version updates

Dependabot checks every ecosystem on Monday at 06:00 in `Europe/Paris`. Each ecosystem has a limit
of five open version-update pull requests. Pull requests receive the `dependencies` label and an
`ecosystem:<name>` label.

Ordinary minor and patch updates are grouped only within their own ecosystem. Major updates remain
individual so that compatibility changes receive a separate review. Security updates are never
included in an ordinary group.

Only stable releases are eligible. Stable repository declarations and `allowPrerelease: false` in
`global.json` form the baseline, and the dependency-automation and supply-chain checks reject a
prerelease opt-in or prerelease inventory entry.

Each Dependabot entry sets `cooldown.default-days` to `3` with no exclusion, so automated version
updates wait three days before a pull request opens. Manually pinned inventory entries are not
subject to any waiting period and may adopt a stable release as soon as it ships.

## Security updates and exceptions

Security updates are intentionally not delayed by the ordinary three-day cooldown and remain in
individual pull requests. They do not bypass review, locked restore, dependency review, static
analysis, vulnerability scanning, or the aggregate CI gate. There is no auto-merge path.

A security fix is pinned in `config/supply-chain.json` like any other entry, with its advisory and
reason recorded. Prereleases, broad ranges, and standing exceptions are not permitted.

## Licences

The OSV scan enforces an allowlist of `MIT`, `Apache-2.0` and `blessing`. A package whose licence
falls outside it is a build failure, and `osv-scanner.toml` is the only place an exception may be
recorded, one package and one version at a time, never as a range.

Two entries exist today. SQLite declares the SPDX-listed SQLite Blessing, which the scanner does
not infer on its own, so its licence is restated rather than waived.
`Microsoft.Testing.Extensions.CodeCoverage` is the single genuine exception: it ships under
MICROSOFT SOFTWARE LICENSE TERMS because it carries a proprietary native coverage engine. It is
accepted only because it is a test-only, build-time dependency that the published Hub binary never
links or redistributes, so it does not reach anyone receiving the Hub under AGPL-3.0. It is
Microsoft's own collector for Microsoft.Testing.Platform, which xunit.v3 4 requires since the .NET
10 SDK dropped the VSTest bridge, and coverlet has no working equivalent there yet. The exception
is pinned to one version, so a bump forces the reasoning to be made again, and it should be dropped
as soon as an SPDX-licensed collector supports the platform.

## Review and activation

Before merging any dependency pull request:

1. Update the supply-chain inventory when the version, digest, commit SHA, release timestamp, or
   official source changes.
2. Run `python3 scripts/check-dependency-automation.py` and the full repository gate.
3. Review major updates separately and verify all lockfiles and immutable pins changed together.
4. Merge manually only after every required check passes.

When the repository becomes public, create the `dependencies` and four `ecosystem:*` labels before
enabling Dependabot version and security updates. GitHub ignores configured labels that do not yet
exist, so label creation is part of activation rather than a claim made by this private repository.

The configuration follows GitHub's current Dependabot options reference:

- <https://docs.github.com/en/code-security/reference/supply-chain-security/dependabot-options-reference>
- <https://docs.github.com/en/code-security/tutorials/secure-your-dependencies/optimizing-pr-creation-version-updates>
- <https://docs.github.com/en/code-security/how-tos/secure-your-supply-chain/manage-your-dependency-security/customizing-dependabot-security-prs>
