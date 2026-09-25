#!/usr/bin/env bash
# Regenerate docs/diagnostics.md from the diagnostic catalog in Offramp.Core.
set -euo pipefail
cd "$(dirname "$0")/.."
OFFRAMP_REGENERATE=1 dotnet test tests/Offramp.Core.Tests --filter "FullyQualifiedName~Diagnostics_document_is_generated_from_the_catalog" --nologo -v q
git --no-pager diff --stat -- docs/diagnostics.md || true
