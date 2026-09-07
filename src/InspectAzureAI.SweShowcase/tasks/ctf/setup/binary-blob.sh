#!/usr/bin/env bash
# Sample 3: the flag is printable text between two runs of random bytes.
set -euo pipefail
mkdir -p challenge
{ head -c 400 /dev/urandom; printf 'picoCTF{str1ngs_h1d3_1n_b1n4ry_bl0bs}'; head -c 400 /dev/urandom; } > challenge/vault.bin
