import { strict as assert } from 'assert';
import * as path from 'path';
import { test } from 'node:test';
import { atLeast, hasLocalTool, resolveServer, versionCommand } from '../src/server';

const repository = path.resolve('/work/repo');
const manifest = path.join(repository, '.config', 'dotnet-tools.json');

test('the setting wins, and a dll runs through dotnet', () => {
  assert.deepEqual(resolveServer({ settingPath: '/opt/offramp/offramp', repository, isFile: () => false }),
    { command: '/opt/offramp/offramp', args: ['ide', 'serve'], source: 'setting' });
  assert.deepEqual(resolveServer({ settingPath: 'C:\\tools\\offramp.dll', repository, isFile: () => false }),
    { command: 'dotnet', args: ['C:\\tools\\offramp.dll', 'ide', 'serve'], source: 'setting' });
});

test('a local tool manifest pins the version the repository uses', () => {
  const readFile = (file: string) => file === manifest ? '{ "version": 1, "tools": { "offramp": { "version": "0.15.0", "commands": ["offramp"] } } }' : undefined;
  assert.deepEqual(resolveServer({ repository, readFile, isFile: () => false }),
    { command: 'dotnet', args: ['tool', 'run', 'offramp', '--', 'ide', 'serve'], source: 'local-tool' });
});

test('else offramp on PATH or in the global tools folder', () => {
  const bin = path.resolve('/usr/local/bin');
  const onPath = resolveServer({ repository, readFile: () => undefined, isFile: f => f === path.join(bin, 'offramp'), env: { PATH: bin, HOME: '/home/me' }, platform: 'linux' });
  const tools = path.join('/home/me', '.dotnet', 'tools', 'offramp');
  const global = resolveServer({ repository, readFile: () => undefined, isFile: f => f === tools, env: { PATH: '', HOME: '/home/me' }, platform: 'linux' });
  const none = resolveServer({ repository, readFile: () => undefined, isFile: () => false, env: { PATH: bin, HOME: '/home/me' }, platform: 'linux' });
  assert.deepEqual(onPath, { command: path.join(bin, 'offramp'), args: ['ide', 'serve'], source: 'global-tool' });
  assert.equal(global?.command, tools);
  assert.equal(none, undefined);
});

test('manifests without offramp, or broken ones, are not local tools', () => {
  assert.equal(hasLocalTool('{ "tools": { "dotnet-ef": {} } }'), false);
  assert.equal(hasLocalTool('not json'), false);
  assert.equal(hasLocalTool(undefined), false);
  assert.equal(hasLocalTool('{ "tools": { "Offramp": {} } }'), true);
});

test('versions compare numerically and the version command reuses the executable', () => {
  assert.equal(atLeast('0.15.0', '0.15.0'), true);
  assert.equal(atLeast('0.16.2', '0.15.0'), true);
  assert.equal(atLeast('0.14.9', '0.15.0'), false);
  assert.equal(atLeast('1.0.0-alpha.0.3', '0.15.0'), true);
  assert.deepEqual(versionCommand({ command: 'dotnet', args: ['tool', 'run', 'offramp', '--', 'ide', 'serve'], source: 'local-tool' }).args,
    ['tool', 'run', 'offramp', '--', '--version']);
});
