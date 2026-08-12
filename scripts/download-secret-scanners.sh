#!/bin/dash
set -eu
/usr/bin/curl -q -fsSL --retry 5 --retry-all-errors --retry-delay 2 https://github.com/gitleaks/gitleaks/releases/download/v8.30.1/gitleaks_8.30.1_linux_x64.tar.gz -o tools/gitleaks.tar.gz
/usr/bin/printf '551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb  tools/gitleaks.tar.gz\n' | /usr/bin/sha256sum -c -
/usr/bin/curl -q -fsSL --retry 5 --retry-all-errors --retry-delay 2 https://github.com/trufflesecurity/trufflehog/releases/download/v3.96.0/trufflehog_3.96.0_linux_amd64.tar.gz -o tools/trufflehog.tar.gz
/usr/bin/printf '7105f1cd6577f058a9e39d0578f1a99c8a1e481e4d3512cd8a09acfe22a0fdc0  tools/trufflehog.tar.gz\n' | /usr/bin/sha256sum -c -
