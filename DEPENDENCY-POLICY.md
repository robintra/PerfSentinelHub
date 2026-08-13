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
