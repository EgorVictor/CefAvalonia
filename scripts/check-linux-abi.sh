#!/usr/bin/env bash
#
# Verify that a Linux ELF binary only requires glibc / libstdc++ symbol
# versions available on the oldest distribution we claim to support.
#
# glibc is forward-compatible only: a binary built on an older distribution
# runs on newer ones, never the other way round. The definitive check is the
# highest versioned symbol the binary imports (GLIBC_2.xx, GLIBCXX_3.4.xx),
# not the distribution it was built on.
#
# Usage:
#   scripts/check-linux-abi.sh <binary> [max-glibc] [max-glibcxx]
#
# Examples:
#   scripts/check-linux-abi.sh CefBrowser.Native/build/Release/CefBrowser.Native
#   scripts/check-linux-abi.sh out/CefBrowser.Native 2.35 3.4.30
#
# Baselines (fully updated systems):
#   Ubuntu 20.04 / Debian 11 : glibc 2.31, GLIBCXX 3.4.28
#   Ubuntu 22.04 / Debian 12 : glibc 2.35 / 2.36, GLIBCXX 3.4.30
#   Ubuntu 24.04            : glibc 2.39, GLIBCXX 3.4.33
#
# Exit code 0 = compatible with the requested baseline, 1 = not compatible.

set -euo pipefail

BIN="${1:-}"
MAX_GLIBC="${2:-2.35}"
MAX_GLIBCXX="${3:-3.4.30}"

if [[ -z "$BIN" ]]; then
  echo "usage: $0 <binary> [max-glibc] [max-glibcxx]" >&2
  exit 2
fi
if [[ ! -f "$BIN" ]]; then
  echo "error: '$BIN' not found" >&2
  exit 2
fi
if ! command -v readelf >/dev/null 2>&1; then
  echo "error: readelf not found (install binutils)" >&2
  exit 2
fi

# Return the highest <prefix>_x.y[.z] version among undefined symbols.
max_symbol_version() {
  local prefix="$1"
  readelf --dyn-syms --wide "$BIN" 2>/dev/null \
    | awk '$7 == "UND" { print }' \
    | grep -oE "${prefix}_[0-9]+(\.[0-9]+)+" \
    | sed "s/^${prefix}_//" \
    | sort -V \
    | tail -n 1
}

# ver_gt A B -> true when A > B
ver_gt() {
  [[ "$1" != "$2" && "$(printf '%s\n%s\n' "$1" "$2" | sort -V | tail -n 1)" == "$1" ]]
}

echo "== Linux ABI check =="
echo "binary      : $BIN"
echo "max glibc   : $MAX_GLIBC"
echo "max GLIBCXX : $MAX_GLIBCXX"
echo

FAILED=0

GLIBC_REQ="$(max_symbol_version GLIBC || true)"
if [[ -z "$GLIBC_REQ" ]]; then
  echo "glibc       : (none) -- static or non-glibc binary"
else
  if ver_gt "$GLIBC_REQ" "$MAX_GLIBC"; then
    echo "glibc       : requires $GLIBC_REQ  >  allowed $MAX_GLIBC  FAIL"
    FAILED=1
  else
    echo "glibc       : requires $GLIBC_REQ  <= allowed $MAX_GLIBC  OK"
  fi
fi

GLIBCXX_REQ="$(max_symbol_version GLIBCXX || true)"
if [[ -z "$GLIBCXX_REQ" ]]; then
  echo "GLIBCXX     : (none) -- statically linked libstdc++ or no C++"
else
  if ver_gt "$GLIBCXX_REQ" "$MAX_GLIBCXX"; then
    echo "GLIBCXX     : requires $GLIBCXX_REQ  >  allowed $MAX_GLIBCXX  FAIL"
    FAILED=1
  else
    echo "GLIBCXX     : requires $GLIBCXX_REQ  <= allowed $MAX_GLIBCXX  OK"
  fi
fi

echo
if [[ "$FAILED" -ne 0 ]]; then
  echo "RESULT: incompatible with the requested baseline." >&2
  echo "Build on the oldest supported distribution (or inside its container), not on a newer one." >&2
  exit 1
fi

echo "RESULT: OK - runs on any distribution with glibc >= $MAX_GLIBC and GLIBCXX >= $MAX_GLIBCXX."
