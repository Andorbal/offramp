/** A `projectMap` entry, as in `offramp.yml`. */
export interface ProjectMapEntry {
  from: string;
  to: string;
}

/** What the server takes as `initializationOptions` (the IdeSettings record in Offramp.Ide). */
export interface ServerSettings {
  enabled: string;
  projectMap: ProjectMapEntry[];
  newCode: { base: string | null; scope: string | null };
  implicitCounterparts: boolean | null;
  codeLens: boolean;
  clientCommands: boolean;
}

/** Reads one `offramp.*` setting (a WorkspaceConfiguration in the extension, a plain map in tests). */
export type SettingReader = <T>(key: string) => T | undefined;

/** The server's settings from the `offramp.*` settings; unset values stay null so `offramp.yml` decides. */
export function serverSettings(read: SettingReader): ServerSettings {
  const map = read<unknown>('projectMap');
  return {
    enabled: read<string>('enabled') ?? 'auto',
    projectMap: Array.isArray(map)
      ? map.filter((e): e is ProjectMapEntry => typeof e?.from === 'string' && typeof e?.to === 'string' && e.from.length > 0 && e.to.length > 0)
      : [],
    newCode: {
      base: nonEmpty(read<string | null>('newCode.base')),
      scope: nonEmpty(read<string | null>('newCode.scope')),
    },
    implicitCounterparts: read<boolean | null>('implicitCounterparts') ?? null,
    codeLens: read<boolean>('codeLens') ?? true,
    clientCommands: true,
  };
}

function nonEmpty(value: string | null | undefined): string | null {
  return typeof value === 'string' && value.trim().length > 0 ? value.trim() : null;
}
