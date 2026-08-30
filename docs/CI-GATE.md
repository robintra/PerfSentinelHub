# Required CI gate activation

`CI / Gate` is trusted only when its expected source is the dedicated GitHub App. A GitHub Actions job from a fork can choose the same display name, so a name-only required status check is not a security boundary.

After the repository becomes public:

1. Create a dedicated GitHub App with repository `Checks: read and write` permission only, disable webhooks, and install it only on this repository.
2. Store its App ID and private key as GitHub Actions secrets named `CI_GATE_APP_ID` and `CI_GATE_APP_PRIVATE_KEY`.
3. Run a trusted internal pull request or an inspected-fork `workflow_dispatch` so the App publishes one `CI / Gate` check.
4. Protect the default branch with required status check `CI / Gate` and select this dedicated GitHub App as the expected source. Never select “any source” or GitHub Actions.
5. Verify with a fork that adds an always-successful GitHub Actions job named `CI / Gate`: the GitHub Actions check must not satisfy the App-bound requirement.

The publisher requests a short-lived installation token restricted to this repository and `checks: write`. There is no `GITHUB_TOKEN` fallback. Until the App is installed, both secrets exist, and the required check is bound to the App, the repository gate is intentionally not considered active.

GitHub documents that a run uses the workflow version at its event SHA/ref and that a required status check can select a specific GitHub App as its expected source:

- <https://docs.github.com/en/actions/concepts/workflows-and-actions/workflows>
- <https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/about-protected-branches>
