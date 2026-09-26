import { execFile } from 'child_process';
import * as vscode from 'vscode';
import { LanguageClient, LanguageClientOptions, ServerOptions } from 'vscode-languageclient/node';
import { hasStateDirectory, repositoryRoot, resolveEnablement } from './enablement';
import { MINIMUM_VERSION, ServerCommand, atLeast, resolveServer, versionCommand } from './server';
import { serverSettings } from './settings';
import { ScanNeed, ServerStatus, statusView } from './status';

/** One language server per workspace folder where Offramp is on (docs/spec/commands/ide.md#vs-code-m15). */
interface Session {
  folder: vscode.WorkspaceFolder;
  repository: string;
  client: LanguageClient;
}

const sessions = new Map<string, Session>();
let output: vscode.LogOutputChannel;
let status: vscode.StatusBarItem;
const statuses = new Map<string, ServerStatus>();
/** Per document URI: why its project needs a scan (offramp/fileStatus), when it does. */
const fileScans = new Map<string, ScanNeed>();

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  output = vscode.window.createOutputChannel('Offramp', { log: true });
  status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 10);
  status.name = 'Offramp';
  context.subscriptions.push(output, status);

  context.subscriptions.push(
    vscode.commands.registerCommand('offramp.move', (argument: { file: string; to: string }) => execute('offramp.move', [argument])),
    vscode.commands.registerCommand('offramp.scan', () => execute('offramp.scan', [])),
    vscode.commands.registerCommand('offramp.refresh', () => execute('offramp.refresh', [])),
    vscode.commands.registerCommand('offramp.restart', async () => {
      await stopAll();
      await syncAll();
    }),
    vscode.commands.registerCommand('offramp.showOutput', () => output.show(true)),
    vscode.commands.registerCommand('offramp.enableInWorkspace', () => setEnabled('on')),
    vscode.commands.registerCommand('offramp.disableInWorkspace', () => setEnabled('off')),
    vscode.workspace.onDidChangeWorkspaceFolders(() => syncAll()),
    vscode.workspace.onDidChangeConfiguration(async e => {
      if (e.affectsConfiguration('offramp.enabled') || e.affectsConfiguration('offramp.server.path')) {
        await stopAll();
        await syncAll();
      }
    }),
    vscode.window.onDidChangeActiveTextEditor(() => renderStatus()),
  );

  await syncAll();
}

export async function deactivate(): Promise<void> {
  await stopAll();
}

/** Starts a client for every folder where Offramp is on, and stops the others. */
async function syncAll(): Promise<void> {
  const folders = vscode.workspace.workspaceFolders ?? [];
  for (const [key, session] of sessions) {
    if (!folders.some(f => f.uri.toString() === key)) {
      await stop(session);
    }
  }

  for (const folder of folders.filter(f => f.uri.scheme === 'file')) {
    const repository = repositoryRoot(folder.uri.fsPath);
    const configuration = vscode.workspace.getConfiguration('offramp', folder.uri);
    const enablement = resolveEnablement(configuration.get<string>('enabled'), hasStateDirectory(repository));
    const key = folder.uri.toString();
    if (!enablement.enabled) {
      const running = sessions.get(key);
      if (running) {
        await stop(running);
      }

      continue;
    }

    if (!sessions.has(key)) {
      await start(folder, repository);
    }
  }

  renderStatus();
}

async function start(folder: vscode.WorkspaceFolder, repository: string): Promise<void> {
  const configuration = vscode.workspace.getConfiguration('offramp', folder.uri);
  const server = resolveServer({ settingPath: configuration.get<string | null>('server.path'), repository });
  if (!server) {
    const install = 'Install offramp';
    const choice = await vscode.window.showWarningMessage(
      'Offramp is on for this repository, but the offramp command-line tool was not found. Install it as a .NET global tool, or set offramp.server.path.',
      install, 'Open settings');
    if (choice === install) {
      const terminal = vscode.window.createTerminal('Offramp');
      terminal.show();
      terminal.sendText('dotnet tool install -g offramp');
    } else if (choice) {
      await vscode.commands.executeCommand('workbench.action.openSettings', 'offramp.server.path');
    }

    return;
  }

  if (server.source !== 'setting') {
    const version = await readVersion(server, repository);
    if (version && !atLeast(version, MINIMUM_VERSION)) {
      void vscode.window.showWarningMessage(
        `Offramp ${version} (${server.source === 'local-tool' ? 'the repository\'s local tool' : 'the global tool'}) is too old for the editor integration, which needs ${MINIMUM_VERSION} or newer.`);
      return;
    }
  }

  // No transport: the client then talks over stdio without appending --stdio (which the server also accepts).
  const serverOptions: ServerOptions = {
    command: server.command,
    args: server.args,
    options: { cwd: repository },
  };
  const pattern = `${folder.uri.fsPath.replace(/\\/g, '/')}/**/*`;
  const clientOptions: LanguageClientOptions = {
    documentSelector: [{ scheme: 'file', language: 'csharp', pattern }],
    workspaceFolder: folder,
    outputChannel: output,
    initializationOptions: serverSettings(key => configuration.get(key)),
    synchronize: {
      configurationSection: 'offramp',
      fileEvents: [
        vscode.workspace.createFileSystemWatcher(new vscode.RelativePattern(folder, '**/*.cs')),
        vscode.workspace.createFileSystemWatcher(new vscode.RelativePattern(folder, '**/*.{csproj,vbproj,fsproj,sln,slnx,slnf,props,targets}')),
        vscode.workspace.createFileSystemWatcher(new vscode.RelativePattern(folder, '**/packages.config')),
        vscode.workspace.createFileSystemWatcher(new vscode.RelativePattern(folder, '**/.offramp/workspace.json')),
        vscode.workspace.createFileSystemWatcher(new vscode.RelativePattern(folder, '**/offramp.yml')),
      ],
    },
  };
  const client = new LanguageClient('offramp', 'Offramp', serverOptions, clientOptions);
  const key = folder.uri.toString();
  sessions.set(key, { folder, repository, client });
  client.onNotification('offramp/status', (value: ServerStatus) => {
    statuses.set(key, value);
    renderStatus();
  });
  client.onNotification('offramp/fileStatus', (value: { uri: string; scan: ScanNeed | null }) => {
    if (value.scan) {
      fileScans.set(value.uri, value.scan);
    } else {
      fileScans.delete(value.uri);
    }

    renderStatus();
  });
  try {
    await client.start();
    output.appendLine(`Offramp started for ${folder.name} (${server.source}: ${server.command} ${server.args.join(' ')}).`);
  } catch (error) {
    sessions.delete(key);
    output.appendLine(`Offramp did not start for ${folder.name}: ${String(error)}`);
    void vscode.window.showErrorMessage(`Offramp did not start: ${String(error)}`, 'Show output').then(c => c && output.show(true));
  }
}

async function stop(session: Session): Promise<void> {
  const key = session.folder.uri.toString();
  sessions.delete(key);
  statuses.delete(key);
  try {
    await session.client.stop();
  } catch (error) {
    output.appendLine(`Offramp did not stop cleanly: ${String(error)}`);
  }

  renderStatus();
}

async function stopAll(): Promise<void> {
  for (const session of [...sessions.values()]) {
    await stop(session);
  }
}

/** Runs a server command in the session of the active document's folder (or the only session). */
async function execute(command: string, args: unknown[]): Promise<unknown> {
  const session = activeSession();
  if (!session) {
    void vscode.window.showInformationMessage('Offramp is not on in this workspace folder. Run "Offramp: Enable in this workspace" to turn it on.');
    return undefined;
  }

  return session.client.sendRequest('workspace/executeCommand', { command, arguments: args });
}

function activeSession(): Session | undefined {
  const document = vscode.window.activeTextEditor?.document.uri;
  const folder = document ? vscode.workspace.getWorkspaceFolder(document) : undefined;
  if (folder && sessions.has(folder.uri.toString())) {
    return sessions.get(folder.uri.toString());
  }

  return sessions.size === 1 ? [...sessions.values()][0] : undefined;
}

async function setEnabled(value: 'on' | 'off'): Promise<void> {
  const document = vscode.window.activeTextEditor?.document.uri;
  const folder = (document ? vscode.workspace.getWorkspaceFolder(document) : undefined) ?? vscode.workspace.workspaceFolders?.[0];
  if (!folder) {
    return;
  }

  await vscode.workspace.getConfiguration('offramp', folder.uri).update('enabled', value, vscode.ConfigurationTarget.WorkspaceFolder);
}

function renderStatus(): void {
  const session = activeSession();
  const value = session ? statuses.get(session.folder.uri.toString()) : undefined;
  if (!session || !value) {
    status.hide();
    return;
  }

  const document = vscode.window.activeTextEditor?.document.uri.toString();
  const view = statusView(value, document ? fileScans.get(document) : undefined);
  status.text = view.text;
  status.tooltip = view.tooltip;
  status.command = view.command;
  status.backgroundColor = view.warning ? new vscode.ThemeColor('statusBarItem.warningBackground') : undefined;
  status.show();
}

function readVersion(server: ServerCommand, cwd: string): Promise<string | undefined> {
  const probe = versionCommand(server);
  return new Promise(resolve => {
    execFile(probe.command, probe.args, { cwd, timeout: 30_000 }, (error, stdout) => resolve(error ? undefined : stdout.trim().split(/\s+/).pop()));
  });
}
