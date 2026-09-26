using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Offramp.Core.Paths;

namespace Offramp.Analysis.Audits.Matchers;

/// <summary>
/// <c>OFR3301</c>–<c>OFR3303</c>: every <c>[DllImport]</c> declared in the project. Each is
/// inventoried (library, entry point, calling convention, character set, <c>SetLastError</c>,
/// marshalled types, whether the library exists only on Windows). String marshalling without
/// an explicit Unicode character set is <c>OFR3302</c>; a blittable signature is a
/// <c>[LibraryImport]</c> candidate (<c>OFR3303</c>).
/// </summary>
public sealed class NativeImportsMatcher : IAuditMatcher
{
    private static readonly HashSet<string> WindowsLibraries = new(StringComparer.OrdinalIgnoreCase)
    {
        "advapi32", "bcrypt", "cfgmgr32", "comctl32", "comdlg32", "credui", "crypt32", "dbghelp", "dnsapi", "dwmapi",
        "gdi32", "gdiplus", "hid", "imm32", "iphlpapi", "kernel32", "kernelbase", "mpr", "msi", "mswsock", "ncrypt",
        "netapi32", "ntdll", "ole32", "oleacc", "oleaut32", "powrprof", "psapi", "rpcrt4", "secur32", "setupapi",
        "shell32", "shlwapi", "user32", "userenv", "uxtheme", "version", "winhttp", "wininet", "winmm", "winspool.drv",
        "wintrust", "wlanapi", "ws2_32", "wtsapi32", "msvcrt", "odbc32", "urlmon", "winscard", "wevtapi",
    };

    private static readonly HashSet<SpecialType> Blittable =
    [
        SpecialType.System_Byte, SpecialType.System_SByte, SpecialType.System_Int16, SpecialType.System_UInt16,
        SpecialType.System_Int32, SpecialType.System_UInt32, SpecialType.System_Int64, SpecialType.System_UInt64,
        SpecialType.System_IntPtr, SpecialType.System_UIntPtr, SpecialType.System_Single, SpecialType.System_Double,
        SpecialType.System_Void,
    ];

    public string Name => "native-imports";

    public IEnumerable<RawFinding> Run(AuditMatchContext context)
    {
        var inventory = context.Rule("OFR3301");
        var ansi = context.Rule("OFR3302");
        var candidate = context.Rule("OFR3303");
        if (inventory is null && ansi is null && candidate is null)
        {
            yield break;
        }

        foreach (var tree in context.Trees)
        {
            var model = context.Compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(declaration) is not IMethodSymbol method || method.GetDllImportData() is not { } import)
                {
                    continue;
                }

                var library = Library(import.ModuleName ?? "");
                var windowsOnly = WindowsLibraries.Contains(library);
                var types = new[] { method.ReturnType }.Concat(method.Parameters.Select(p => p.Type)).Select(t => t.ToDisplayString()).Distinct(StringComparer.Ordinal).ToList();
                var name = AuditEngine.Name(method);
                var location = declaration.Identifier.GetLocation();
                var details = new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["callingConvention"] = import.CallingConvention.ToString(),
                    ["charSet"] = import.CharacterSet.ToString(),
                    ["entryPoint"] = import.EntryPointName ?? method.Name,
                    ["library"] = import.ModuleName ?? "",
                    ["marshalledTypes"] = string.Join(", ", types),
                    ["setLastError"] = import.SetLastError ? "true" : "false",
                    ["windowsOnly"] = windowsOnly ? "true" : "false",
                };

                if (inventory is not null)
                {
                    var where = windowsOnly ? $"{import.ModuleName}, a Windows library" : import.ModuleName;
                    yield return new RawFinding(inventory, location, name, $"{name} calls {details["entryPoint"]} in {where}.", details) { Namespace = AuditEngine.NamespaceOf(method) };
                }

                var strings = StringParameters(method);
                var unicode = import.CharacterSet == System.Runtime.InteropServices.CharSet.Unicode;
                if (ansi is not null && strings.Count > 0 && !unicode)
                {
                    var how = import.CharacterSet is System.Runtime.InteropServices.CharSet.Auto
                        ? "with CharSet.Auto, which is ANSI off Windows"
                        : "as ANSI (the default CharSet)";
                    yield return new RawFinding(ansi, location, name, $"{name} marshals {string.Join(", ", strings)} {how}.", details)
                    {
                        Namespace = AuditEngine.NamespaceOf(method),
                    };
                }
                else if (candidate is not null && IsBlittable(method))
                {
                    yield return new RawFinding(candidate, location, name, $"{name} has a blittable signature; [LibraryImport] generates its marshalling at compile time.", details)
                    {
                        Namespace = AuditEngine.NamespaceOf(method),
                    };
                }
            }
        }
    }

    /// <summary>A library name as the loader probes it: lower case, without <c>.dll</c>.</summary>
    private static string Library(string module)
    {
        var name = Path.GetFileName(module.Replace('\\', '/'));
        return name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>String and character parameters (or return) marshalled without an explicit <c>[MarshalAs]</c>.</summary>
    private static List<string> StringParameters(IMethodSymbol method)
    {
        var result = new List<string>();
        if (IsText(method.ReturnType) && !method.GetReturnTypeAttributes().Any(IsMarshalAs))
        {
            result.Add("the return value");
        }

        result.AddRange(method.Parameters.Where(p => IsText(p.Type) && !p.GetAttributes().Any(IsMarshalAs)).Select(p => $"'{p.Name}'"));
        return result;
    }

    private static bool IsMarshalAs(AttributeData attribute) =>
        attribute.AttributeClass?.ToDisplayString() == "System.Runtime.InteropServices.MarshalAsAttribute";

    private static bool IsText(ITypeSymbol type) =>
        type.SpecialType is SpecialType.System_String or SpecialType.System_Char
        || type.ToDisplayString() == "System.Text.StringBuilder"
        || (type is IArrayTypeSymbol array && IsText(array.ElementType));

    private static bool IsBlittable(IMethodSymbol method) =>
        new[] { method.ReturnType }.Concat(method.Parameters.Select(p => p.Type)).All(t => Blittable.Contains(t.SpecialType) || t is IPointerTypeSymbol)
        && method.Parameters.All(p => p.RefKind == RefKind.None);
}

/// <summary>
/// <c>OFR3310</c> (with the rule's symbols and attributes): <c>COMReference</c> items in the
/// project file. COM exists only on Windows.
/// </summary>
public sealed class ComReferencesMatcher : IAuditMatcher
{
    public string Name => "com-references";

    public IEnumerable<RawFinding> Run(AuditMatchContext context)
    {
        if (context.Rule("OFR3310") is not { } rule || context.Project.ComReferences.Count == 0)
        {
            yield break;
        }

        var lines = ItemLines(RepoPaths.ToAbsolute(context.RepositoryRoot, context.Project.Id));
        foreach (var reference in context.Project.ComReferences.OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            var details = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["embedInteropTypes"] = reference.EmbedInteropTypes ? "true" : "false" };
            if (reference.Guid is { } guid)
            {
                details["guid"] = guid;
            }

            yield return new RawFinding(rule, Location.None, reference.Name, $"The project references the COM library {reference.Name}.", details)
            {
                FileLocation = (context.Project.Id, lines.GetValueOrDefault(reference.Name, 1)),
            };
        }
    }

    /// <summary>COMReference item name → line in the project file.</summary>
    private static Dictionary<string, int> ItemLines(string projectPath)
    {
        var lines = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            var document = XDocument.Load(projectPath, LoadOptions.SetLineInfo);
            foreach (var item in document.Descendants().Where(e => e.Name.LocalName == "COMReference"))
            {
                if (item.Attribute("Include")?.Value is { } name && !lines.ContainsKey(name))
                {
                    lines[name] = ((IXmlLineInfo)item).LineNumber;
                }
            }
        }
        catch (Exception e) when (e is XmlException or IOException)
        {
            // The line stays 1.
        }

        return lines;
    }
}
