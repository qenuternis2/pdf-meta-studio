#!/usr/bin/env bash
# Загружает qpdf, XMP-Toolkit-SDK и Expat по версиям из scripts/deps.lock в worker/external
# и применяет обязательный патч XMP SDK. Повторный запуск безопасен.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
ext="$root/worker/external"
mkdir -p "$ext"
grep -v '^\s*#' "$root/scripts/deps.lock" | while read -r name url tag commit; do
  [ -n "${name:-}" ] || continue
  dir="$ext/$name"
  if [ ! -d "$dir/.git" ]; then
    git -c core.autocrlf=false -c advice.detachedHead=false clone --quiet --depth 1 --branch "$tag" "$url" "$dir"
  fi
  actual="$(git -C "$dir" rev-parse HEAD)"
  if [ "$actual" != "$commit" ]; then
    echo "ОШИБКА: $name: ожидался коммит $commit, получен $actual" >&2
    exit 1
  fi
  echo "$name $tag $commit OK"
done
patch="$root/scripts/patches/xmp-keep-translations.patch"
if git -C "$ext/xmp" apply --reverse --check "$patch" 2>/dev/null; then
  echo "Патч XMP уже применён"
else
  git -C "$ext/xmp" apply "$patch"
  echo "Патч XMP применён"
fi
