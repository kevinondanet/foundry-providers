#!/usr/bin/env bash
# gzip: the flag is inside a gzip-compressed text file.
set -euo pipefail
mkdir -p challenge
printf 'meeting notes\n- rotate the vault key\n- flag: picoCTF{c0mpr3ss10n_1s_n0t_c0nc34lm3nt}\n' | gzip -c > challenge/notes.gz
