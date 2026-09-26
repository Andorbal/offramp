import { strict as assert } from 'assert';
import * as path from 'path';
import { test } from 'node:test';
import { hasStateDirectory, repositoryRoot, resolveEnablement } from '../src/enablement';

test('auto follows the .offramp folder and the setting overrides it', () => {
  assert.deepEqual(resolveEnablement('auto', false), { mode: 'auto', enabled: false, reason: 'no-state-directory' });
  assert.deepEqual(resolveEnablement(undefined, true), { mode: 'auto', enabled: true, reason: 'state-directory' });
  assert.deepEqual(resolveEnablement('on', false), { mode: 'on', enabled: true, reason: 'setting-on' });
  assert.deepEqual(resolveEnablement('off', true), { mode: 'off', enabled: false, reason: 'setting-off' });
});

test('the repository root is the nearest folder with .git, else the folder', () => {
  const root = path.resolve('/work/repo');
  const exists = (p: string) => p === path.join(root, '.git');
  assert.equal(repositoryRoot(path.join(root, 'src', 'Foo'), exists), root);
  assert.equal(repositoryRoot(path.resolve('/elsewhere/folder'), () => false), path.resolve('/elsewhere/folder'));
});

test('the state directory is looked for at the repository root', () => {
  const root = path.resolve('/work/repo');
  assert.equal(hasStateDirectory(root, p => p === path.join(root, '.offramp')), true);
  assert.equal(hasStateDirectory(root, () => false), false);
});
