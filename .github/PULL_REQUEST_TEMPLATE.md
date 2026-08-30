## Purpose

Describe the problem and the smallest change that solves it.

## Verification

- [ ] `make verify-fast` passes locally.
- [ ] Relevant security checks pass with `make security` when dependencies, workflows, release code, or trust boundaries change.
- [ ] New behavior has a regression test that failed before the implementation.
- [ ] Every commit is signed and reports a verified signature.
- [ ] No secret, credential, private finding, or generated report is included.
- [ ] Documentation and operator commands match the implementation.
- [ ] Diagrams and launcher screenshots regenerated when a screen or a flow changed (see `CONTRIBUTING.md`).

## Trust boundary

Fork pull requests run without repository secrets and cannot publish the required `CI / Gate` check. A maintainer must inspect the current fork head and run the trusted review workflow for that exact commit. Do not request or add secrets to make a fork check pass.

## Security impact

Describe changes to authentication, authorization, input validation, dependencies, workflows, release publication, or stored data. Write “None” only after checking each area.
