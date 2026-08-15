#!/bin/dash
set -eu
/usr/bin/curl -q -fsSL --retry 5 --retry-all-errors --retry-delay 2 https://github.com/rhysd/actionlint/releases/download/v1.7.12/actionlint_1.7.12_linux_amd64.tar.gz -o tools/actionlint.tar.gz
/usr/bin/printf '8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8  tools/actionlint.tar.gz\n' | /usr/bin/sha256sum -c -
/usr/bin/curl -q -fsSL --retry 5 --retry-all-errors --retry-delay 2 https://github.com/zizmorcore/zizmor/releases/download/v1.29.0/zizmor-x86_64-unknown-linux-gnu.tar.gz -o tools/zizmor.tar.gz
/usr/bin/printf 'dd96df044a6e8538d5f423790f453bdd03d49e5b2bcc38214acc41a2f1297839  tools/zizmor.tar.gz\n' | /usr/bin/sha256sum -c -
/usr/bin/curl -q -fsSL --retry 5 --retry-all-errors --retry-delay 2 https://github.com/astral-sh/ruff/releases/download/0.16.3/ruff-x86_64-unknown-linux-gnu.tar.gz -o tools/ruff.tar.gz
/usr/bin/printf '7ab3b978d2c0b1c96b2323d4e5c4f35284ae1cdf35d2f7399595c74c805f5fa3  tools/ruff.tar.gz\n' | /usr/bin/sha256sum -c -
