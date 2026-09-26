import { strict as assert } from 'assert';
import { test } from 'node:test';
import { statusView } from '../src/status';

const counterparts = [
  { project: 'src/Foo/Foo.csproj', counterparts: ['src/ModernF/ModernF.csproj'], source: 'map' },
  { project: 'src/Legacy/Legacy.csproj', counterparts: [], source: 'none' },
];

test('a missing model asks for a scan', () => {
  const view = statusView({ enabled: true, reason: 'state-directory', model: 'missing', counterparts: [], message: null });
  assert.equal(view.command, 'offramp.scan');
  assert.equal(view.warning, true);
});

test('a fresh model lists each project and its counterparts', () => {
  const view = statusView({ enabled: true, reason: 'state-directory', model: 'fresh', counterparts, message: null });
  assert.equal(view.text, '$(check) Offramp');
  assert.match(view.tooltip, /1 of 2 \.NET Framework projects have a portable counterpart/);
  assert.match(view.tooltip, /• Foo → ModernF/);
  assert.match(view.tooltip, /• Legacy → \(none: add a projectMap entry\)/);
});

test('a stale model still works and offers a scan', () => {
  const view = statusView({ enabled: true, reason: 'state-directory', model: 'stale', counterparts, message: 'The workspace model is stale (1 changed).' });
  assert.equal(view.command, 'offramp.scan');
  assert.match(view.tooltip, /stale/);
});

test('repository problems (a map entry that names no project) show in the tooltip', () => {
  const view = statusView({
    enabled: true, reason: 'state-directory', model: 'fresh', counterparts, message: null,
    problems: [{ code: 'OFR6002', message: "projectMap entry Foo → Nope: 'Nope' is not a project of the workspace model." }],
  });
  assert.equal(view.text, '$(warning) Offramp');
  assert.match(view.tooltip, /OFR6002: projectMap entry Foo → Nope/);
});

test('a file whose project needs a scan says so, and a click scans', () => {
  const status = { enabled: true, reason: 'state-directory', model: 'fresh', counterparts, message: null };
  const view = statusView(status, {
    reason: 'new-project',
    project: 'src/Billing/Billing.csproj',
    message: 'src/Billing/Billing.csproj was added after the last scan, so Offramp cannot check it yet.',
  });
  assert.equal(view.text, '$(sync) Offramp: scan Billing');
  assert.equal(view.command, 'offramp.scan');
  assert.equal(view.warning, true);
  assert.match(view.tooltip, /added after the last scan/);
  assert.equal(statusView(status).text, '$(check) Offramp');
});
