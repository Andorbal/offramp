import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

/** How to start `offramp ide serve`, and where the executable came from. */
export interface ServerCommand {
  command: string;
  args: string[];
  source: 'setting' | 'local-tool' | 'global-tool';
}

export interface ServerLookup {
  /** `offramp.server.path`, when set. */
  settingPath?: string | null;
  /** The repository root, where a local tool manifest may pin the version the team uses. */
  repository: string;
  readFile?: (file: string) => string | undefined;
  /** Whether an executable exists at this path. */
  isFile?: (file: string) => boolean;
  env?: NodeJS.ProcessEnv;
  platform?: NodeJS.Platform;
}

const SERVE = ['ide', 'serve'];

/**
 * Finds the server (docs/spec/commands/ide.md#vs-code-m15): the setting; else the repository's
 * local tool (`.config/dotnet-tools.json` lists `offramp`), so the editor runs the version the
 * repository pins; else `offramp` on `PATH` or in the global tools folder. Undefined when none.
 */
export function resolveServer(lookup: ServerLookup): ServerCommand | undefined {
  const readFile = lookup.readFile ?? readFileOrUndefined;
  const isFile = lookup.isFile ?? isFileOnDisk;
  const env = lookup.env ?? process.env;
  const platform = lookup.platform ?? process.platform;

  const setting = lookup.settingPath?.trim();
  if (setting) {
    return setting.toLowerCase().endsWith('.dll')
      ? { command: 'dotnet', args: [setting, ...SERVE], source: 'setting' }
      : { command: setting, args: SERVE, source: 'setting' };
  }

  if (hasLocalTool(readFile(path.join(lookup.repository, '.config', 'dotnet-tools.json')))) {
    return { command: 'dotnet', args: ['tool', 'run', 'offramp', '--', ...SERVE], source: 'local-tool' };
  }

  const names = platform === 'win32' ? ['offramp.exe', 'offramp.cmd', 'offramp'] : ['offramp'];
  const separator = platform === 'win32' ? ';' : ':';
  const home = env.USERPROFILE ?? env.HOME ?? os.homedir();
  const directories = [...(env.PATH ?? env.Path ?? '').split(separator).filter(d => d.length > 0), path.join(home, '.dotnet', 'tools')];
  for (const directory of directories) {
    for (const name of names) {
      const candidate = path.join(directory, name);
      if (isFile(candidate)) {
        return { command: candidate, args: SERVE, source: 'global-tool' };
      }
    }
  }

  return undefined;
}

/** True when a dotnet tool manifest lists the `offramp` tool. */
export function hasLocalTool(manifest: string | undefined): boolean {
  if (!manifest) {
    return false;
  }

  try {
    const tools = (JSON.parse(manifest) as { tools?: Record<string, unknown> }).tools ?? {};
    return Object.keys(tools).some(name => name.toLowerCase() === 'offramp');
  } catch {
    return false;
  }
}

/** The command line that prints the version of the same executable. */
export function versionCommand(server: ServerCommand): ServerCommand {
  return { ...server, args: [...server.args.slice(0, server.args.length - SERVE.length), '--version'] };
}

/** The oldest Offramp with `ide serve`. */
export const MINIMUM_VERSION = '0.15.0';

/** True when `version` (x.y.z, maybe with a prerelease suffix) is at least `minimum`. */
export function atLeast(version: string, minimum: string): boolean {
  const parse = (v: string) => v.trim().split(/[-+]/)[0].split('.').map(p => Number.parseInt(p, 10) || 0);
  const [a, b] = [parse(version), parse(minimum)];
  for (let i = 0; i < 3; i++) {
    if ((a[i] ?? 0) !== (b[i] ?? 0)) {
      return (a[i] ?? 0) > (b[i] ?? 0);
    }
  }

  return true;
}

function readFileOrUndefined(file: string): string | undefined {
  try {
    return fs.readFileSync(file, 'utf8');
  } catch {
    return undefined;
  }
}

function isFileOnDisk(file: string): boolean {
  try {
    return fs.statSync(file).isFile();
  } catch {
    return false;
  }
}
