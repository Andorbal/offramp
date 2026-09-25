#!/usr/bin/env bash
# Accept every pending Verify snapshot (*.received.* -> *.verified.*).
# Review `git diff` afterwards: a snapshot that changed only in ordering means a
# command lost determinism, which is a bug to fix, not a snapshot to accept.
set -euo pipefail
cd "$(dirname "$0")/.."
find tests -name '*.received.*' -print0 | while IFS= read -r -d '' file; do
  mv -f "$file" "${file/.received./.verified.}"
  echo "accepted ${file/.received./.verified.}"
done
