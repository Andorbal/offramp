# Regenerate docs/diagnostics.md from the diagnostic catalog in Offramp.Core.
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$env:OFFRAMP_REGENERATE = '1'
try {
    dotnet test tests/Offramp.Core.Tests --filter "FullyQualifiedName~Diagnostics_document_is_generated_from_the_catalog" --nologo -v q
} finally {
    Remove-Item Env:OFFRAMP_REGENERATE
}
