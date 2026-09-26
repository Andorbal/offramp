#!/usr/bin/env bash
# Prepares the integration test's workspace: a committed, scanned copy of the ide-counterpart
# fixture with a new class in Foo, and offramp.server.path pointing at the offramp.dll to test.
#   OFFRAMP_DLL=path/to/offramp.dll test/integration/prepare.sh DIRECTORY
set -euo pipefail
repo="$(cd "$(dirname "$0")/../../../.." && pwd)"
target="${1:?usage: prepare.sh DIRECTORY}"
dll="${OFFRAMP_DLL:-$repo/src/Offramp.Cli/bin/Release/net10.0/offramp.dll}"

rm -rf "$target"
cp -r "$repo/tests/fixtures/ide-counterpart" "$target"
cd "$target"
git init -q
git config user.email tests@offramp.test
git config user.name "Offramp Tests"
git add -A
git commit -q -m fixture
dotnet "$dll" scan --json > scan.json
rm scan.json

mkdir -p .vscode
printf '{ "offramp.server.path": "%s" }\n' "$dll" > .vscode/settings.json
cat > src/Foo/Pricing/TaxRule.cs <<'CS'
namespace Foo.Pricing
{
    public sealed class TaxRule
    {
        public decimal Apply(decimal amount, decimal rate) => amount * (1 + rate);
    }
}
CS
echo "$target"
