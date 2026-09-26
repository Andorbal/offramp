import { strict as assert } from 'assert';
import * as path from 'path';
import * as vscode from 'vscode';

/**
 * docs/spec/commands/ide.md#vs-code-m15, end to end: the extension starts `offramp ide serve` for
 * a repository with .offramp/, and a new class in a .NET Framework project gets the OFR6001
 * diagnostic, a move lens for each counterpart, and a quick fix.
 */
export async function run(): Promise<void> {
  const folder = vscode.workspace.workspaceFolders![0].uri.fsPath;
  const file = vscode.Uri.file(path.join(folder, 'src', 'Foo', 'Pricing', 'TaxRule.cs'));
  await vscode.window.showTextDocument(await vscode.workspace.openTextDocument(file));

  const diagnostics = await until(
    async () => vscode.languages.getDiagnostics(file).filter(d => d.source === 'offramp'),
    found => found.length > 0,
    'the OFR6001 diagnostic');
  const diagnostic = diagnostics[0];
  assert.equal(typeof diagnostic.code === 'object' ? diagnostic.code.value : diagnostic.code, 'OFR6001');
  assert.equal(diagnostic.severity, vscode.DiagnosticSeverity.Information);
  assert.equal(diagnostic.range.start.line, 2);

  const lenses = await until(
    async () => (await vscode.commands.executeCommand<vscode.CodeLens[]>('vscode.executeCodeLensProvider', file)) ?? [],
    found => found.some(l => l.command?.title.startsWith('Offramp') ?? false),
    'the move lenses');
  assert.deepEqual(lenses.map(l => l.command?.title).filter(t => t?.startsWith('Offramp')), ['Offramp: move to ModernF', 'Offramp: move to Shared']);
  assert.equal(lenses[0].command?.command, 'offramp.move');

  const actions = (await vscode.commands.executeCommand<vscode.CodeAction[]>('vscode.executeCodeActionProvider', file, diagnostic.range)) ?? [];
  const quickFix = actions.find(a => a.title === 'Move TaxRule.cs to ModernF (Offramp)' && a.kind?.value === 'quickfix');
  assert.ok(quickFix, `no quick fix among: ${actions.map(a => a.title).join(', ')}`);
  assert.deepEqual(quickFix.command?.arguments, [{ file: 'src/Foo/Pricing/TaxRule.cs', to: 'src/ModernF/ModernF.csproj' }]);
}

async function until<T>(read: () => Promise<T>, done: (value: T) => boolean, what: string, timeoutMs = 180_000): Promise<T> {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const value = await read();
    if (done(value)) {
      return value;
    }

    if (Date.now() > deadline) {
      throw new Error(`Timed out waiting for ${what}.`);
    }

    await new Promise(resolve => setTimeout(resolve, 500));
  }
}
