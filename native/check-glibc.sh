#!/bin/sh
# Fails when a Linux library needs a newer glibc than the given baseline (usage: check-glibc.sh lib.so 2.28).
set -e
need=$(objdump -T "$1" | grep -o 'GLIBC_[0-9.]*' | sed 's/GLIBC_//' | sort -uV | tail -1)
max=$(printf '%s\n%s\n' "$need" "$2" | sort -V | tail -1)
echo "$1 : glibc $need requise (plafond $2)"
[ "$max" = "$2" ]
