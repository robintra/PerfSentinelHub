# Releasing PerfSentinel Hub

PerfSentinel Hub publishes stable `v0.MINOR.PATCH` releases only. Publication promotes the exact
artifacts verified by the build workflow; it does not check out source, rebuild, or repackage them.

## Trust anchors

The signed Git tag must match the single Ed25519 identity in
`config/signing-identities.json`. Its current principal is `robin.trassard@gmail.com` and its
fingerprint is `SHA256:PXKuwB2z5bZZ3oIp+fY+Xz6iV03G/e2xkgzJJxieD4k`. The repository contains
the public key only.

Cosign bundles and GitHub attestations must use all of these values exactly:

- OIDC issuer: `https://token.actions.githubusercontent.com`
- Repository: `robintra/PerfSentinelHub`
- Workflow: `.github/workflows/release.yml`
- Source ref: `refs/tags/v0.MINOR.PATCH`
- Certificate identity:
  `https://github.com/robintra/PerfSentinelHub/.github/workflows/release.yml@refs/tags/v0.MINOR.PATCH`

Any identity, repository, workflow, ref, source commit, subject, or digest mismatch blocks the
release.

## Lab validation gate

No version is tagged without a recent PASS from the simulation lab. The lab runs the Hub image
against a real daemon through `hub-ingestion`, `hub-derived-status`, `hub-lineage-mutation`,
`hub-retention-purge`, `hub-source-reachability` and `hub-plugin-contract`, which is coverage no
unit or integration test in this repository reaches: they exercise ingestion, polling, the derived
status, the lineage columns and the plugin's envelope contract against a daemon that really
produced the findings.

The gate is operator-driven by design. CI cannot reproduce a lab run, which needs a Kubernetes
cluster, a workload fleet and an image built from the commit under test.

1. In the perf-sentinel-simulation-lab checkout, seed the Hub image from this repository with
   `make seed-hub-local`, then run `make verify-all-scenarios`.
2. Produce the ledger line with `scripts/record-validation.sh vX.Y.Z PASS` and append it to
   `release-gate/lab-validations.txt` here. Note the Hub commit the image was built from in a
   comment above it: the version in column one is the one about to be tagged, so the commit is
   what identifies the content that was actually validated.
3. Assert the gate before tagging:

   ```bash
   release-gate/check-lab-validation.sh --version vX.Y.Z
   ```

   It exits non-zero when there is no PASS for that version, when the newest one is older than
   thirty days, or when the ledger is unreadable.

The script is a copy of perf-sentinel's, which is the original and carries the full test suite.
Keep the two in step: a fix to the date handling or the ledger schema belongs in both.

## Publish a release

1. Confirm that `main` is clean, synchronized, protected, and public. Run `make release-check`.
   Assert the lab validation gate above for the version you are about to tag.
2. Create the signed tag with `scripts/release.sh v0.MINOR.PATCH`. The local script uploads no
   artifact.
3. Wait for every build, duplicate-build comparison, native smoke test, scan, signature,
   attestation, and closed-manifest check to pass.
4. Review the workflow summary and approve the `hub-release` environment manually. Publication
   permissions and the automatic GitHub token are unavailable to all preceding jobs.
5. The protected job checks the workflow artifact ID and SHA-256 digest, downloads that artifact
   without a source checkout, revalidates `release-manifest.json` and `SHA256SUMS`, promotes the
   image by immutable digest, publishes the unchanged chart package, and creates the GitHub
   Release from the same closed asset set.
6. Require the post-publication workflow to pass on Linux AMD64, Linux ARM64, macOS ARM64, and
   Windows AMD64 before announcing the release.

Do not approve publication if the tag, source commit, artifact ID, artifact digest, or protected
environment differs from the reviewed workflow run.

## Verify publicly without a secret

Accept either the stable tag or its exact public GitHub Release URL:

```text
python3 scripts/verify-release.py public-input v0.1.0
python3 scripts/verify-release.py public-input https://github.com/robintra/PerfSentinelHub/releases/tag/v0.1.0
```

After downloading every public release asset into `release/`, validate its closed checksum and
manifest set:

```text
python3 scripts/verify-release.py verify-published --root release
```

For each subject named in `release-manifest.json`, verify both public trust systems using the exact
identity above:

```text
cosign verify-blob --bundle release/ARTIFACT.sigstore.json \
  --certificate-identity https://github.com/robintra/PerfSentinelHub/.github/workflows/release.yml@refs/tags/v0.1.0 \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com release/ARTIFACT

gh attestation verify release/ARTIFACT --bundle release/release.provenance.sigstore.json \
  --repo robintra/PerfSentinelHub \
  --cert-identity https://github.com/robintra/PerfSentinelHub/.github/workflows/release.yml@refs/tags/v0.1.0 \
  --cert-oidc-issuer https://token.actions.githubusercontent.com \
  --source-ref refs/tags/v0.1.0 --source-digest SOURCE_COMMIT --deny-self-hosted-runners
```

The daily workflow performs these checks without a configured secret, starts all four public
native binaries on matching GitHub-hosted runners, starts the image by digest, and ensures the
digest-bound chart renders the same image digest.

## Roll back

Do not move, delete, or reuse a signed release tag. Roll an affected deployment back to the prior
known-good image and chart digests, then publish a new patch version. If distribution must stop
while the patch is prepared, make the GitHub Release a draft and delete only the compromised
registry manifests by their recorded digests:

```text
gh release edit v0.1.0 --draft
oras manifest delete ghcr.io/robintra/perf-sentinel-hub@sha256:IMAGE_DIGEST
oras manifest delete ghcr.io/robintra/charts/perf-sentinel-hub@sha256:CHART_DIGEST
```

These commands are destructive. Confirm the exact release and digests from the incident record
before running them. Keep the signed tag and published verification evidence for auditability.

## Respond to compromise

1. Disable the `hub-release` environment and stop release workflow runs.
2. Record the affected tags, source commits, workflow run IDs, artifact IDs, image digest, chart
   digest, and first known exposure time. Never paste tokens or private material into an issue.
3. Draft the affected releases and remove compromised registry manifests by exact digest.
4. Rotate any affected GitHub credentials. Keyless Cosign uses short-lived GitHub OIDC
   certificates, so there is no Cosign private key to recover from this repository.
5. If the Git tag key is affected, replace the public key and fingerprint in
   `config/signing-identities.json` through the protected review path, revoke the old key in the
   maintainer's signing setup, and reject new releases made by it.
6. If a workflow identity is affected, change the workflow only through a reviewed signed commit
   and update the closed identity configuration. Do not broaden the certificate identity regex.
7. Publish a new signed patch release from a known-good commit, verify it publicly, and only then
   re-enable the protected environment.

The scheduled alert issue contains only a fixed failure sentence and a workflow-run URL. It is
closed only after a later complete success.
