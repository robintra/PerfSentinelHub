#!/usr/bin/env bash
set -euo pipefail

# Turn each WebM Playwright recorded into a GIF under docs/img/hub/.
# Run through `npm run demo` from tests/browser.

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
BROWSER_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_ROOT="$(cd "$BROWSER_DIR/../.." && pwd)"
OUT_DIR="$REPO_ROOT/docs/img/hub"

mkdir -p "$OUT_DIR"

convert_webm() {
  local webm="$1" out="$2"
  echo "build-gif: $webm -> $out"
  ffmpeg -y -i "$webm" -vf \
    "fps=20,scale=1000:-1:flags=lanczos,split[s0][s1];[s0]palettegen=max_colors=96[p];[s1][p]paletteuse=dither=bayer:bayer_scale=5" \
    -loop 0 "$out" 2> /tmp/hub-build-gif.log
  echo "build-gif: wrote $out ($(du -k "$out" | awk '{print $1}') KB)"
}

shopt -s nullglob
found=0
for webm in "$BROWSER_DIR"/demo-videos/*/video.webm; do
  found=1
  dir="$(basename "$(dirname "$webm")")"
  case "$dir" in
    *launcher-dark)  convert_webm "$webm" "$OUT_DIR/launcher_dark.gif"  ;;
    *launcher-light) convert_webm "$webm" "$OUT_DIR/launcher_light.gif" ;;
    *) echo "build-gif: cannot map project for $webm" >&2; exit 1 ;;
  esac
done

# A silent no-op reads as success and ships yesterday's GIFs.
if [[ "$found" -eq 0 ]]; then
  echo "build-gif: no webm under $BROWSER_DIR/demo-videos/" >&2
  exit 1
fi
