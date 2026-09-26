/** The server's `offramp/status` notification (docs/spec/commands/ide.md#offramp-ide-serve). */
export interface ServerStatus {
  enabled: boolean;
  reason: string;
  model: 'missing' | 'stale' | 'fresh' | 'off' | 'invalid-config' | string;
  counterparts: { project: string; counterparts: string[]; source: string }[];
  /** Repository-level findings: project map entries that do not resolve, an unavailable new-code base. */
  problems?: { code: string; message: string }[];
  message: string | null;
}

/** Why a file's project needs a scan (`offramp/fileStatus`, docs/spec/commands/ide.md#projects-that-need-a-scan). */
export interface ScanNeed {
  reason: 'new-project' | 'no-compilation' | 'project-changed' | string;
  project: string;
  message: string;
}

/** What the status bar shows, and what clicking it does. */
export interface StatusView {
  text: string;
  tooltip: string;
  command: 'offramp.scan' | 'offramp.showOutput';
  warning: boolean;
}

/** The status bar for the repository, and for the active file when its project needs a scan. */
export function statusView(status: ServerStatus, fileScan?: ScanNeed): StatusView {
  if (fileScan && (status.model === 'fresh' || status.model === 'stale')) {
    return {
      text: `$(sync) Offramp: scan ${name(fileScan.project)}`,
      tooltip: `${fileScan.message}\nClick to run offramp scan (it builds the solution).`,
      command: 'offramp.scan',
      warning: true,
    };
  }

  const projects = status.counterparts ?? [];
  const mapped = projects.filter(p => p.counterparts.length > 0);
  const summary = projects.length === 0
    ? 'No .NET Framework-only projects.'
    : `${mapped.length} of ${projects.length} .NET Framework project${projects.length === 1 ? '' : 's'} have a portable counterpart:\n`
      + projects.map(p => `• ${name(p.project)} → ${p.counterparts.length === 0 ? '(none: add a projectMap entry)' : p.counterparts.map(name).join(', ')}`).join('\n');
  switch (status.model) {
    case 'missing':
      return { text: '$(warning) Offramp: scan needed', tooltip: status.message ?? 'No workspace model yet. Click to run offramp scan.', command: 'offramp.scan', warning: true };
    case 'stale':
      return { text: '$(sync) Offramp', tooltip: `${status.message ?? 'The workspace model is stale.'}\nClick to scan again.\n\n${summary}`, command: 'offramp.scan', warning: false };
    case 'invalid-config':
      return { text: '$(error) Offramp', tooltip: status.message ?? 'offramp.yml has problems; run offramp doctor.', command: 'offramp.showOutput', warning: true };
    case 'off':
      return { text: '$(circle-slash) Offramp off', tooltip: 'Offramp is off in this repository.', command: 'offramp.showOutput', warning: false };
    default: {
      const problems = status.problems ?? [];
      const listed = problems.length === 0 ? '' : '\n\n' + problems.map(p => `${p.code}: ${p.message}`).join('\n');
      return {
        text: problems.length === 0 ? '$(check) Offramp' : '$(warning) Offramp',
        tooltip: `Offramp is checking new code in .NET Framework projects.\n\n${summary}${listed}`,
        command: 'offramp.showOutput',
        warning: false,
      };
    }
  }
}

function name(project: string): string {
  const file = project.split('/').pop() ?? project;
  return file.replace(/\.(cs|vb|fs)proj$/i, '');
}
