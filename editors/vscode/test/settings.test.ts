import { strict as assert } from 'assert';
import { test } from 'node:test';
import { serverSettings } from '../src/settings';

test('unset settings stay null so offramp.yml decides', () => {
  const settings = serverSettings(() => undefined);
  assert.deepEqual(settings, {
    enabled: 'auto',
    projectMap: [],
    newCode: { base: null, scope: null },
    implicitCounterparts: null,
    codeLens: true,
    clientCommands: true,
  });
});

test('set values pass through and incomplete map entries are dropped', () => {
  const values: Record<string, unknown> = {
    enabled: 'on',
    projectMap: [{ from: 'Foo', to: 'ModernF' }, { from: 'Bar' }, { from: '', to: 'X' }],
    'newCode.base': ' origin/main ',
    'newCode.scope': '',
    implicitCounterparts: false,
    codeLens: false,
  };
  const settings = serverSettings(<T>(key: string) => values[key] as T | undefined);
  assert.deepEqual(settings.projectMap, [{ from: 'Foo', to: 'ModernF' }]);
  assert.deepEqual(settings.newCode, { base: 'origin/main', scope: null });
  assert.equal(settings.enabled, 'on');
  assert.equal(settings.implicitCounterparts, false);
  assert.equal(settings.codeLens, false);
});
