#!/bin/dash
set -eu
/usr/bin/curl -q -fsSL https://github.com/gitleaks/gitleaks/releases/download/v8.30.1/gitleaks_8.30.1_linux_x64.tar.gz -o tools/gitleaks.tar.gz
/usr/bin/printf '551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb  tools/gitleaks.tar.gz\n' | /usr/bin/sha256sum -c -
/usr/bin/curl -q -fsSL https://github.com/google/osv-scanner/releases/download/v2.5.0/osv-scanner_linux_amd64 -o tools/osv-scanner
/usr/bin/printf 'edcfc41d257db36148f065055655fe3fcfc434b0b423ea67468a84c207524e0c  tools/osv-scanner\n' | /usr/bin/sha256sum -c -
/usr/bin/curl -q -fsSL https://github.com/anchore/syft/releases/download/v1.50.0/syft_1.50.0_linux_amd64.tar.gz -o tools/syft.tar.gz
/usr/bin/printf 'bf7b29ff57f06da30918266a0e1c2885a8f99784798d1bdb1628886aa015d788  tools/syft.tar.gz\n' | /usr/bin/sha256sum -c -
/usr/bin/curl -q -fsSL https://github.com/zizmorcore/zizmor/releases/download/v1.29.0/zizmor-x86_64-unknown-linux-gnu.tar.gz -o tools/zizmor.tar.gz
/usr/bin/printf 'dd96df044a6e8538d5f423790f453bdd03d49e5b2bcc38214acc41a2f1297839  tools/zizmor.tar.gz\n' | /usr/bin/sha256sum -c -
