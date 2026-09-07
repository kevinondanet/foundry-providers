#!/usr/bin/env bash
# base64: the flag is base64-encoded.
set -euo pipefail
mkdir -p challenge
printf '%s' 'picoCTF{b4s3_s1xty_f0ur_1s_n0t_3ncrypt10n}' | base64 > challenge/encoded.txt
