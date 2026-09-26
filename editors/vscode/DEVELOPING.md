# Developing the VS Code extension

The extension is a thin client of `offramp ide serve` (docs/spec/commands/ide.md): it decides
whether Offramp is on, finds the server, starts one language client per workspace folder, and
shows the server's status. Everything it displays comes from the server.

```bash
npm ci
npm test                      # compiles, then the unit tests (node --test)

# End to end in a real VS Code, against a scanned copy of tests/fixtures/ide-counterpart:
dotnet build ../../src/Offramp.Cli -c Release -f net10.0
test/integration/prepare.sh /tmp/ide-workspace          # OFFRAMP_DLL=... to test another build
OFFRAMP_TEST_WORKSPACE=/tmp/ide-workspace xvfb-run -a npm run test:integration   # drop xvfb-run on a desktop

npx vsce package --out offramp.vsix
```

To try it by hand, open this folder in VS Code, press F5 (Extension Development Host), open a
repository with `.offramp/`, and set `offramp.server.path` to a local `offramp.dll` if the
installed tool is older than 0.15.0.
