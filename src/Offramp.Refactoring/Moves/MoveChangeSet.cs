using Offramp.Core.Model;
using Offramp.Core.Paths;
using Offramp.Refactoring.ChangeSets;
using Offramp.Refactoring.ProjectFiles;

namespace Offramp.Refactoring.Moves;

/// <summary>
/// Turns a move plan into a change set against the project files as they are now: the
/// plan's project edits, the item bookkeeping each moved file needs (explicit Compile and
/// EmbeddedResource items, Designer metadata), and the renames. Moved files are only renamed.
/// </summary>
public static class MoveChangeSet
{
    /// <param name="root">The repository root.</param>
    /// <param name="plan">The plan.</param>
    /// <param name="skip">Moves left out (files changed since the plan).</param>
    /// <param name="edited">The project files the change set edits.</param>
    /// <param name="model">The workspace model, for central package management and AssemblyInfo files; null uses neither.</param>
    /// <param name="created">Project files the change set creates (<c>move extract</c>), by path: their content before the move's edits.</param>
    public static ChangeSet Build(string root, MovePlanDocument plan, IReadOnlySet<string> skip, out IReadOnlyList<string> edited, WorkspaceModel? model = null,
        IReadOnlyDictionary<string, byte[]>? created = null)
    {
        var editors = new SortedDictionary<string, (byte[] Before, ProjectFileEditor Editor)>(StringComparer.Ordinal);
        ProjectFileEditor Editor(string project)
        {
            if (!editors.TryGetValue(project, out var entry))
            {
                var bytes = created is not null && created.TryGetValue(project, out var content) ? content : File.ReadAllBytes(RepoPaths.ToAbsolute(root, project));
                entry = (bytes, ProjectFileEditor.Load(bytes));
                editors[project] = entry;
            }

            return entry.Editor;
        }

        var changeSet = new ChangeSet();
        var moves = plan.Moves.Where(m => !skip.Contains(m.File)).ToList();
        if (moves.Count == 0)
        {
            edited = [];
            return changeSet;
        }

        // Created projects come from `created`, solutions from MoveApplier.AddToSolutionsAsync.
        foreach (var edit in plan.ProjectEdits.Where(e => e.Kind is not (ProjectEditKind.CreateProject or ProjectEditKind.AddToSolution)))
        {
            var editor = Editor(edit.Project);
            switch (edit.Kind)
            {
                case ProjectEditKind.AddProjectReference:
                    editor.AddProjectReference(Relative(edit.Project, edit.Value!));
                    break;
                case ProjectEditKind.AddPackageReference:
                    editor.AddPackageReference(edit.Value!, Central(model, edit.Project) ? null : edit.Version);
                    break;
                case ProjectEditKind.RemovePackageReference:
                    editor.RemoveItems("PackageReference", edit.Value!);
                    break;
                case ProjectEditKind.AddInternalsVisibleTo when editor.IsSdkStyle:
                    editor.AddInternalsVisibleTo(edit.Value!);
                    break;
                case ProjectEditKind.AddInternalsVisibleTo:
                    AddToAssemblyInfo(root, model, edit, changeSet);
                    break;
                case ProjectEditKind.KeepResourceName:
                    editor.AddUpdate("EmbeddedResource", Relative(edit.Project, edit.Value!), [KeyValuePair.Create("LogicalName", edit.Version!)]);
                    break;
                default:
                    break;
            }
        }

        foreach (var move in moves)
        {
            var from = Editor(plan.From);
            var to = Editor(plan.To);
            var fromPath = Relative(plan.From, move.File);
            var toPath = Relative(plan.To, move.To);
            var type = move.File.EndsWith(".resx", StringComparison.OrdinalIgnoreCase) ? "EmbeddedResource" : "Compile";
            from.RemoveItems(type, fromPath);
            if (!to.IsSdkStyle)
            {
                if (type == "Compile")
                {
                    to.AddCompile(toPath);
                }
                else
                {
                    to.AddEmbeddedResource(toPath);
                }
            }

            // Designer metadata (DependentUpon, Generator) follows the file.
            foreach (var (itemType, metadata) in from.UpdatesFor(fromPath))
            {
                to.AddUpdate(itemType, toPath, metadata);
            }

            from.RemoveUpdates(fromPath);
            changeSet.Rename(root, move.File, move.To);
        }

        foreach (var project in (created?.Keys ?? []).Order(StringComparer.Ordinal))
        {
            changeSet.Creates.Add(new FileCreate(project, editors.TryGetValue(project, out var entry) ? entry.Editor.Save() : created![project]));
        }

        foreach (var (project, (before, editor)) in editors.Where(e => created is null || !created.ContainsKey(e.Key)))
        {
            changeSet.Edit(project, before, editor.Save());
        }

        edited = [.. changeSet.Edits.Select(e => e.Path).Order(StringComparer.Ordinal)];
        return changeSet;
    }

    private static void AddToAssemblyInfo(string root, WorkspaceModel? model, ProjectEdit edit, ChangeSet changeSet)
    {
        var info = model?.Projects.FirstOrDefault(p => p.Id == edit.Project)?.Compile.FirstOrDefault(f => f.EndsWith("/AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase));
        if (info is null)
        {
            return;
        }

        var bytes = File.ReadAllBytes(RepoPaths.ToAbsolute(root, info));
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var line = $"[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"{edit.Value}\")]";
        changeSet.Edit(info, bytes, [.. bytes, .. System.Text.Encoding.UTF8.GetBytes((text.EndsWith('\n') ? "" : newline) + line + newline)]);
    }

    private static bool Central(WorkspaceModel? model, string project) =>
        model?.Projects.FirstOrDefault(p => p.Id == project) is { } info
        && info.Properties.TryGetValue("ManagePackageVersionsCentrally", out var value)
        && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>A repository path relative to a project's folder, with backslashes as project files write them.</summary>
    internal static string Relative(string fromProject, string to)
    {
        var slash = fromProject.LastIndexOf('/');
        var fromParts = slash < 0 ? [] : fromProject[..slash].Split('/');
        var toParts = to.Split('/');
        var common = 0;
        while (common < fromParts.Length && common < toParts.Length - 1 && string.Equals(fromParts[common], toParts[common], StringComparison.Ordinal))
        {
            common++;
        }

        return string.Join('\\', Enumerable.Repeat("..", fromParts.Length - common).Concat(toParts.Skip(common)));
    }
}
