#!/usr/bin/env bash

set -euo pipefail
export LC_ALL=C

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPOSITORY="$(cd "${SCRIPT_DIR}/.." && pwd)"
TAG=""
DRY_RUN=0
SIGNING_SCRATCH=""

fail() {
  printf 'release: %s\n' "$*" >&2
  exit 1
}

usage() {
  printf 'Usage: %s v0.MINOR.PATCH [--dry-run]\n' "$(basename "$0")" >&2
  exit 2
}

cleanup() {
  if [ -n "${SIGNING_SCRATCH}" ] && [ -d "${SIGNING_SCRATCH}" ]; then
    rm -rf -- "${SIGNING_SCRATCH}"
  fi
}
trap cleanup EXIT HUP INT TERM

while [ "$#" -gt 0 ]; do
  case "$1" in
    --dry-run)
      [ "${DRY_RUN}" -eq 0 ] || usage
      DRY_RUN=1
      ;;
    -*) usage ;;
    *)
      [ -z "${TAG}" ] || usage
      TAG="$1"
      ;;
  esac
  shift
done

[ -n "${TAG}" ] || usage
[[ "${TAG}" =~ ^v0\.[0-9]+\.[0-9]+$ ]] || fail "tag must be a stable tag matching v0.MINOR.PATCH without a suffix"
VERSION="${TAG#v}"

cd "${REPOSITORY}"
[ -f "PerfSentinelHub/PerfSentinelHub.csproj" ] || fail "must run from a PerfSentinelHub checkout"

current_branch() {
  git symbolic-ref --quiet --short HEAD 2>/dev/null || true
}

ensure_main() {
  local branch
  branch="$(current_branch)"
  [ "${branch}" = "main" ] || fail "release must run from main; current branch is '${branch:-detached}'"
}

ensure_clean() {
  [ -z "$(git status --porcelain=v1 --untracked-files=all)" ] || fail "working tree must be clean"
}

ensure_synchronized_main() {
  local listing local_sha
  if ! listing="$(git ls-remote --exit-code --heads origin refs/heads/main 2>/dev/null)"; then
    fail "origin/main cannot be resolved without mutating local refs"
  fi
  if [[ ! "${listing}" =~ ^([0-9a-f]{40})[[:space:]]refs/heads/main$ ]]; then
    fail "origin/main returned an ambiguous ref"
  fi
  local_sha="$(git rev-parse --verify refs/heads/main)"
  [ "${local_sha}" = "${BASH_REMATCH[1]}" ] || fail "local main and origin/main must be synchronized exactly"
}

ensure_tag_absent() {
  local status
  if git show-ref --verify --quiet "refs/tags/${TAG}"; then
    fail "tag ${TAG} already exists locally"
  fi
  set +e
  git ls-remote --exit-code --tags origin "refs/tags/${TAG}" >/dev/null 2>&1
  status=$?
  set -e
  case "${status}" in
    0) fail "tag ${TAG} already exists on origin" ;;
    2) ;;
    *) fail "origin tag state cannot be verified" ;;
  esac
}

verify_signing_identity() {
  local signing_key signing_format allowed_signers key value
  signing_key="$(git config --get user.signingkey 2>/dev/null || true)"
  [ -n "${signing_key}" ] || fail "a verifiable signing identity is required in user.signingkey"
  signing_format="$(git config --get gpg.format 2>/dev/null || printf 'openpgp')"
  if [ "${signing_format}" = "ssh" ]; then
    allowed_signers="$(git config --path --get gpg.ssh.allowedSignersFile 2>/dev/null || true)"
    [ -n "${allowed_signers}" ] && [ -f "${allowed_signers}" ] || fail "SSH signing identity requires a readable gpg.ssh.allowedSignersFile"
  fi

  SIGNING_SCRATCH="$(mktemp -d "${TMPDIR:-/tmp}/perf-sentinel-hub-signing.XXXXXX")" || fail "cannot create signing probe"
  git -C "${SIGNING_SCRATCH}" init -q -b main
  git -C "${SIGNING_SCRATCH}" config user.name "PerfSentinelHub release preflight"
  git -C "${SIGNING_SCRATCH}" config user.email "release-preflight@example.invalid"
  git -C "${SIGNING_SCRATCH}" config user.signingkey "${signing_key}"
  git -C "${SIGNING_SCRATCH}" config gpg.format "${signing_format}"
  for key in gpg.program gpg.ssh.program gpg.x509.program; do
    value="$(git config --get "${key}" 2>/dev/null || true)"
    if [ -n "${value}" ]; then
      git -C "${SIGNING_SCRATCH}" config "${key}" "${value}"
    fi
  done
  if [ "${signing_format}" = "ssh" ]; then
    git -C "${SIGNING_SCRATCH}" config gpg.ssh.allowedSignersFile "${allowed_signers}"
  fi
  git -C "${SIGNING_SCRATCH}" commit --allow-empty -q -m probe
  if ! git -C "${SIGNING_SCRATCH}" tag -s signature-probe -m signature-probe >/dev/null 2>&1 \
    || ! git -C "${SIGNING_SCRATCH}" verify-tag signature-probe >/dev/null 2>&1; then
    fail "configured signing identity cannot create and verify a signed tag"
  fi
  cleanup
  SIGNING_SCRATCH=""
}

ensure_main
ensure_clean
ensure_synchronized_main
ensure_tag_absent
verify_signing_identity
make release-check VERSION="${VERSION}"

short_sha="$(git rev-parse --short=12 HEAD)"
if [ "${DRY_RUN}" -eq 1 ]; then
  printf 'release: dry-run passed; no repository or remote mutation\n'
  printf 'release: would create signed tag %s at %s, push main, then push the tag\n' "${TAG}" "${short_sha}"
  exit 0
fi

printf 'Type %s to confirm the signed tag and pushes: ' "${TAG}"
IFS= read -r confirmation || fail "confirmation was not provided"
[ "${confirmation}" = "${TAG}" ] || fail "confirmation did not exactly match ${TAG}; nothing was mutated"

ensure_main
ensure_clean
ensure_synchronized_main
ensure_tag_absent
make release-check VERSION="${VERSION}"

if ! git tag -s "${TAG}" -m "PerfSentinelHub ${TAG}"; then
  fail "signed tag creation failed"
fi
if ! git verify-tag "${TAG}" >/dev/null 2>&1; then
  git tag -d "${TAG}" >/dev/null 2>&1 || true
  fail "new tag signature could not be verified; local tag was removed"
fi
[ "$(git rev-list -n 1 "${TAG}")" = "$(git rev-parse refs/heads/main)" ] \
  || { git tag -d "${TAG}" >/dev/null 2>&1 || true; fail "tag target is not the main commit"; }

if ! git push origin refs/heads/main:refs/heads/main; then
  git tag -d "${TAG}" >/dev/null 2>&1 || true
  fail "main push failed; local tag was removed"
fi
if ! git push origin "refs/tags/${TAG}:refs/tags/${TAG}"; then
  fail "tag push failed; inspect local and remote tag state before retrying"
fi

printf 'release: pushed signed tag %s at %s\n' "${TAG}" "${short_sha}"
