#!/usr/bin/env bash
# D-20 (audit P15): verify that a publish tree carries the VCOM loader set.
#
# Why this exists: a publish tree without loaders/ packages perfectly well - the app, the fastboot
# binary, the udev rule and the docs are all there - and then fails at VCOM-unlock time with
# "Loader directory for <cpu> not found", which reads like a host permission problem instead of a
# packaging bug. This script makes that failure happen at build time, with a readable reason.
#
# Usage: packaging/check-loaders.sh PUBLISH_DIR [SOURCE_DIR]
#   PUBLISH_DIR  a tree produced by packaging/publish.sh (must contain loaders/<chip>/<stage>.ktl)
#   SOURCE_DIR   the authoritative asset tree (default: $KIRIN_LOADERS_SRC, else <repo>/loaders)
#
# Exit codes: 0 ok, 1 the publish tree is missing/incorrect loaders, 2 the source tree is unusable.
# Hardware-free: directories and file names only, no USB, no udev, no device.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
PUBLISH="${1:-}"
SOURCE="${2:-${KIRIN_LOADERS_SRC:-$HERE/../loaders}}"

if [ -z "$PUBLISH" ]; then
  echo "usage: check-loaders.sh PUBLISH_DIR [SOURCE_DIR]" >&2
  exit 2
fi

if [ ! -d "$SOURCE" ]; then
  echo "✗ loader source tree not found: $SOURCE" >&2
  echo "  (expected the repository's loaders/ directory, one subdirectory per SoC)" >&2
  exit 2
fi

chips=0
files=0
missing=0
for chip_dir in "$SOURCE"/*/; do
  [ -d "$chip_dir" ] || continue
  chip="$(basename "$chip_dir")"
  # Chips without a single .ktl are not an asset (empty directory left behind by a checkout).
  ktl_count="$(find "$chip_dir" -maxdepth 1 -name '*.ktl' -type f | wc -l | tr -d ' ')"
  [ "$ktl_count" -gt 0 ] || continue
  chips=$((chips + 1))
  files=$((files + ktl_count))
  target="$PUBLISH/loaders/$chip"
  if [ ! -d "$target" ]; then
    echo "✗ missing loader directory in the publish tree: loaders/$chip" >&2
    missing=$((missing + 1))
    continue
  fi
  for source_file in "$chip_dir"*.ktl; do
    [ -f "$source_file" ] || continue
    name="$(basename "$source_file")"
    if [ ! -f "$target/$name" ]; then
      echo "✗ missing loader file in the publish tree: loaders/$chip/$name" >&2
      missing=$((missing + 1))
    fi
  done
done

if [ "$chips" -eq 0 ]; then
  echo "✗ loader source tree has no <chip>/<stage>.ktl files: $SOURCE" >&2
  exit 2
fi

if [ "$missing" -ne 0 ]; then
  echo "" >&2
  echo "✗ the publish tree does not carry the VCOM loader set ($missing problem(s)): $PUBLISH" >&2
  echo "  consequence: vcom-unlock/vcom_unlock would report \"Loader directory for <cpu> not found\" at runtime," >&2
  echo "  which is a packaging bug, NOT a host permission problem." >&2
  echo "  fix: make sure the source archive contains loaders/ and that packaging/publish.sh ran on it" >&2
  echo "  (the csproj copies loaders/**/* into the publish tree), or pass the asset tree explicitly." >&2
  exit 1
fi

echo "loaders ok: $chips chips, $files .ktl files under $PUBLISH/loaders"
