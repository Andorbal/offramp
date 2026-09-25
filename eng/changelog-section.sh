#!/usr/bin/env bash
# Print the CHANGELOG.md section for a version (without the "v" prefix).
# Usage: eng/changelog-section.sh 0.3.0 [CHANGELOG.md]
set -euo pipefail

version="${1:?version required, e.g. 0.3.0}"
file="${2:-CHANGELOG.md}"

# Section starts at "## [<version>]" and ends before the next "## [" heading
# or the link-reference block at the bottom.
awk -v ver="$version" '
  $0 ~ "^## \\[" ver "\\]" { inside = 1; next }
  inside && /^## \[/        { exit }
  inside && /^\[.*\]: http/ { exit }
  inside                    { print }
' "$file" | sed -e :a -e '/^\n*$/{$d;N;ba' -e '}'
