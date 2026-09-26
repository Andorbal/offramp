import * as fs from 'fs';
import * as path from 'path';

/** The `offramp.enabled` setting (docs/spec/commands/ide.md#when-it-is-on). */
export type EnabledMode = 'auto' | 'on' | 'off';

export interface Enablement {
  mode: EnabledMode;
  enabled: boolean;
  reason: 'setting-on' | 'setting-off' | 'state-directory' | 'no-state-directory';
}

/** The directory whose presence turns Offramp on in `auto` mode. */
export const STATE_DIRECTORY = '.offramp';

/** The same rule the language server applies: `on` and `off` decide; `auto` follows `.offramp/` at the repository root. */
export function resolveEnablement(mode: string | undefined, hasStateDirectory: boolean): Enablement {
  switch ((mode ?? 'auto').trim().toLowerCase()) {
    case 'on':
      return { mode: 'on', enabled: true, reason: 'setting-on' };
    case 'off':
      return { mode: 'off', enabled: false, reason: 'setting-off' };
    default:
      return hasStateDirectory
        ? { mode: 'auto', enabled: true, reason: 'state-directory' }
        : { mode: 'auto', enabled: false, reason: 'no-state-directory' };
  }
}

/** The top of the git work tree that holds `folder` (the nearest ancestor with `.git`), else the folder itself. */
export function repositoryRoot(folder: string, exists: (p: string) => boolean = fs.existsSync): string {
  let current = path.resolve(folder);
  for (;;) {
    if (exists(path.join(current, '.git'))) {
      return current;
    }

    const parent = path.dirname(current);
    if (parent === current) {
      return path.resolve(folder);
    }

    current = parent;
  }
}

export function hasStateDirectory(repository: string, isDirectory: (p: string) => boolean = isDirectoryOnDisk): boolean {
  return isDirectory(path.join(repository, STATE_DIRECTORY));
}

function isDirectoryOnDisk(p: string): boolean {
  try {
    return fs.statSync(p).isDirectory();
  } catch {
    return false;
  }
}
