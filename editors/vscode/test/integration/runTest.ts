import * as path from 'path';
import { runTests } from '@vscode/test-electron';

/**
 * Runs the integration suite in a real VS Code against the workspace `prepare.sh` makes: a
 * scanned, committed copy of tests/fixtures/ide-counterpart with a new class in it.
 */
async function main(): Promise<void> {
  const workspace = process.env.OFFRAMP_TEST_WORKSPACE;
  if (!workspace) {
    throw new Error('Set OFFRAMP_TEST_WORKSPACE to the directory test/integration/prepare.sh prepared.');
  }

  await runTests({
    version: process.env.OFFRAMP_TEST_VSCODE ?? 'stable',
    extensionDevelopmentPath: path.resolve(__dirname, '..', '..', '..'),
    extensionTestsPath: path.resolve(__dirname, 'suite'),
    launchArgs: [workspace, '--disable-extensions', '--disable-workspace-trust'],
  });
}

main().catch(error => {
  console.error(error);
  process.exit(1);
});
