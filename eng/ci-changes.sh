#!/usr/bin/env bash
# Decide whether a change needs CI's build, test, and pack jobs (.github/workflows/ci.yml).
# Usage: eng/ci-changes.sh BASE HEAD
# Prints "code=true" or "code=false" (for $GITHUB_OUTPUT), and why on stderr.
#
# A change is docs-only (code=false) when it touches at least one file and every file it
# touches is added or modified, not deleted or renamed, and is either a top-level *.md file or
# under docs/. Two docs files are test inputs and always count as code: docs/diagnostics.md
# (DiagnosticCatalogTests) and docs/spec/03-configuration.md (ConfigLoaderTests). A base that
# is empty, all zeros (a new branch), or missing from the history (a force push) gives nothing
# to judge, so everything runs.
set -euo pipefail

base="${1-}"
head="${2:?usage: eng/ci-changes.sh BASE HEAD}"

needs_build() {
  echo "code=true"
  echo "CI builds: $1" >&2
  exit 0
}

is_docs() {
  case "$1" in
    docs/diagnostics.md | docs/spec/03-configuration.md) return 1 ;;
    docs/*) return 0 ;;
    */*) return 1 ;;
    *.md) return 0 ;;
    *) return 1 ;;
  esac
}

if [[ -z "$base" || "$base" =~ ^0+$ ]]; then
  needs_build "no base revision to compare with"
fi
if ! git cat-file -e "$base^{commit}" 2>/dev/null; then
  needs_build "base $base is not in the history"
fi

count=0
while IFS=$'\t' read -r status path; do
  count=$((count + 1))
  if [[ "$status" != "A" && "$status" != "M" ]]; then
    needs_build "$path ($status)"
  fi
  if ! is_docs "$path"; then
    needs_build "$path"
  fi
done < <(git diff --name-status --no-renames "$base" "$head")

if [[ "$count" -eq 0 ]]; then
  needs_build "no changed files"
fi

echo "code=false"
if [[ "$count" -eq 1 ]]; then files="1 file"; else files="$count files"; fi
echo "Docs only ($files): build, test, and pack jobs are skipped." >&2
