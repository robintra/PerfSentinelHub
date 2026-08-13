# Security policy

## Supported versions

PerfSentinelHub is in the `0.x` development series. Only the latest published `0.1.x` release receives security fixes. Older releases and untagged builds are unsupported; upgrade to the newest patch before reporting behavior that may already be fixed.

## Report a vulnerability privately

Use [GitHub private vulnerability reporting](https://github.com/robintra/PerfSentinelHub/security/advisories/new). Do not open a public issue, pull request, discussion, or commit containing vulnerability details. Do not include production credentials or private finding data in any report.

Include the affected version or commit, impact, prerequisites, minimal reproduction, and any proposed remediation. Sanitize logs and test data. You may use a harmless proof of concept, but do not access data you do not own, disrupt services, or test third-party installations without permission.

Maintainers will acknowledge a report within three business days, provide an initial assessment within seven business days, and coordinate disclosure and credit with the reporter. These are response targets, not a promise that every fix will be complete within that period. If the private reporting form is unavailable, open the “Security reporting help” issue without technical details and request a private channel.

## Disclosure and release

Keep the report confidential until a fixed release and advisory are ready or a disclosure date has been agreed. A security release follows the same signed, verified release process as other releases.

Public release artifacts are checksummed, signed with Sigstore, and accompanied by GitHub attestations. Follow `RELEASING.md` to verify the workflow identity, source commit, and artifact digest independently.
