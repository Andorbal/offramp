namespace Offramp.Core.Model;

/// <summary>
/// Whether a project uses the Windows desktop stack (Windows Forms or WPF), and so moves to a
/// <c>-windows</c> target with the Windows desktop framework. The project's kind is not enough:
/// kind says what the project is (a <c>winforms</c> application), and most forms and controls
/// live in class libraries.
/// </summary>
public static class WindowsDesktop
{
    /// <summary>The .NET Framework assemblies of Windows Forms and WPF.</summary>
    public static readonly IReadOnlySet<string> Assemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "System.Windows.Forms", "PresentationFramework", "PresentationCore", "WindowsBase", "System.Xaml", "UIAutomationProvider",
    };

    /// <summary>
    /// True for a <c>winforms</c> or <c>wpf</c> project, one that sets <c>UseWindowsForms</c> or
    /// <c>UseWPF</c>, and one that references a Windows Forms or WPF assembly.
    /// </summary>
    public static bool Uses(ProjectInfo project) =>
        project.Kind is ProjectKind.Winforms or ProjectKind.Wpf
        || IsTrue(project, "UseWindowsForms")
        || IsTrue(project, "UseWPF")
        || project.AssemblyReferences.Any(r => Assemblies.Contains(r.Name));

    private static bool IsTrue(ProjectInfo project, string property) =>
        project.Properties.TryGetValue(property, out var value) && string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase);
}
