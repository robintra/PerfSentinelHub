# Contributing to PerfSentinelHub

Thank you for contributing. Keep changes focused, preserve the AGPL-3.0-only license, and avoid adding dependencies or abstractions unless the existing platform cannot meet the requirement.

## Before you start

Use .NET SDK 10.0.400, Python 3, Docker, Helm 4, and the security tools used by the relevant Make targets. Package restores are locked. Do not edit generated `bin`, `obj`, coverage, or release output.

For a defect or behavior change, write a test that demonstrates the failure before changing production code. Run the narrow test during development, then run:

```bash
make verify-fast
```

Before requesting review, run the broader gate when your platform has its required native and container tools:

```bash
make verify
make security
```

`make verify-fast` restores locked tools and packages, checks formatting, runs Python and .NET tests, enforces coverage, and validates analysis and secret metadata. `make verify` adds NativeAOT, dependency audit, image scanning, and Helm validation. `make security` checks dependency vulnerabilities, temporary security exceptions, analysis configuration, and supply-chain declarations. Explain any platform-specific command you could not run; do not claim it passed.

`make release-check VERSION=0.1.0` validates repository version consistency before a
signed tag is created.

The protected GitHub check is `CI / Gate`, from the dedicated PerfSentinel CI Gate App. A
GitHub Actions check of the same name does not satisfy that App-backed boundary.

The toolchain is pinned: .NET SDK 10.0.400, ASP.NET and SQLite 10.0.11, SQLitePCLRaw
3.0.5, Helm 4.2.3, and SHA-pinned GitHub Actions (checkout 7.0.1, setup-dotnet 6.0.0,
setup-helm 5.0.1, Trivy Action 0.36.0). Runtime containers are non-root, read-only, and
based on digest-pinned official NativeAOT and chiseled images.

## Commit and pull request rules

Create a topic branch, keep commits reviewable, and sign every commit. Configure an SSH or GPG signing key supported by GitHub, then verify locally before pushing:

```bash
git config commit.gpgsign true
git log --show-signature -1
```

Open a pull request against `main`, complete the pull request checklist, and resolve every review conversation. The repository requires linear history and disallows direct pushes, force pushes, branch deletion, and administrator bypass. The solo-maintainer policy requires zero additional approving reviews, but it still requires a pull request, signed commits, resolved conversations, and every required check.

Required checks include the dedicated-App-authored `CI / Gate`, SonarCloud, CodeQL, and dependency review. A check named `CI / Gate` from GitHub Actions or any other source does not satisfy the dedicated App boundary.

## Fork trust boundary

Fork pull requests run secret-free validation. Fork code never receives repository secrets and cannot publish the required `CI / Gate` check. After inspecting the current head commit, a maintainer may run the trusted workflow with the exact pull request number and full head SHA. A changed head requires a new inspection and dispatch. Never add a secret to a fork workflow, use `pull_request_target` to execute fork code, or weaken a check to make an untrusted run pass.

## Security reports

Do not report vulnerabilities in a public issue or pull request. Follow `SECURITY.md` and use GitHub private vulnerability reporting. Remove credentials, private findings, and sensitive logs from ordinary bug reports.

## Documentation and licensing

Update operator documentation when commands, configuration, trust boundaries, or supported artifacts change. Contributions are accepted under the repository's AGPL-3.0-only license; by submitting a contribution, you confirm that you have the right to provide it under that license.
