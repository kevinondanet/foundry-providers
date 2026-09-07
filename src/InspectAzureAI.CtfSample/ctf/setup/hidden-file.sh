#!/usr/bin/env bash
# hidden-file: the flag sits in a dotfile under a cache directory, among decoy files.
set -euo pipefail
mkdir -p challenge/.cache/logs challenge/src challenge/docs
printf 'picoCTF{h1dd3n_f1l3s_4r3_st1ll_f1l3s}\n' > challenge/.cache/logs/.flag
printf '# challenge\n\nNothing to see here.\n' > challenge/docs/README.md
printf 'print("hello")\n' > challenge/src/app.py
printf '2026-09-05 boot ok\n' > challenge/.cache/logs/boot.log
