using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;

namespace Offramp.Scaffolding.Config;

/// <summary>
/// The C# that <c>config convert</c> writes: options classes and the ConfigurationManager
/// shim. Both compile as C# 7.3 on .NET Framework and modern .NET (block namespaces, classes
/// with setters, no nullable annotations), marked auto-generated.
/// </summary>
public static class ConfigTemplates
{
    public static string Options(string ns, IReadOnlyList<string> appSettingKeys, IReadOnlyList<(string Key, SectionSchema Schema)> sections)
    {
        var builder = new StringBuilder();
        Header(builder, "the options classes for appsettings.json");
        builder.Append(CultureInfo.InvariantCulture, $"namespace {ns}\n{{\n");
        builder.Append("    /// <summary>The appSettings, which appsettings.json holds as root keys: bind with configuration.Get&lt;AppSettingsOptions&gt;().</summary>\n");
        builder.Append("    public class AppSettingsOptions\n    {\n");
        var first = true;
        foreach (var key in appSettingKeys)
        {
            builder.Append(first ? "" : "\n");
            first = false;
            if (SyntaxFacts.IsValidIdentifier(key) && SyntaxFacts.GetKeywordKind(key) == SyntaxKind.None)
            {
                builder.Append(CultureInfo.InvariantCulture, $"        public string {key} {{ get; set; }}\n");
            }
            else
            {
                builder.Append(CultureInfo.InvariantCulture, $"        // \"{key}\" is not a C# name: read configuration[\"{key}\"].\n");
            }
        }

        builder.Append("    }\n");
        var written = new HashSet<string>(StringComparer.Ordinal) { "AppSettingsOptions" };
        foreach (var (key, schema) in sections)
        {
            Class(builder, schema, $"The {schema.TypeName} section, under \"{key}\" in appsettings.json: bind with configuration.GetSection({schema.OptionsName}.Section).", key, written);
        }

        builder.Append("}\n");
        return builder.ToString();
    }

    public static string Shim(string ns) =>
        Header("ConfigurationManager's AppSettings and ConnectionStrings over IConfiguration") + $$"""
        namespace {{ns}}
        {
            /// <summary>
            /// ConfigurationManager.AppSettings and ConnectionStrings, read from IConfiguration, so
            /// code that has no constructor to take IConfiguration keeps working while it moves.
            /// Call Initialize at startup; `offramp codemod run --mod config-manager-shim` points
            /// ConfigurationManager call sites here.
            /// </summary>
            public static class ConfigurationManagerShim
            {
                private static global::Microsoft.Extensions.Configuration.IConfiguration _configuration;

                public static void Initialize(global::Microsoft.Extensions.Configuration.IConfiguration configuration)
                {
                    _configuration = configuration ?? throw new global::System.ArgumentNullException(nameof(configuration));
                }

                public static AppSettingsShim AppSettings => new AppSettingsShim(Configuration);

                public static ConnectionStringsShim ConnectionStrings => new ConnectionStringsShim(Configuration);

                private static global::Microsoft.Extensions.Configuration.IConfiguration Configuration =>
                    _configuration ?? throw new global::System.InvalidOperationException("Call ConfigurationManagerShim.Initialize(configuration) at startup.");
            }

            /// <summary>AppSettings: root keys of the configuration.</summary>
            public sealed class AppSettingsShim
            {
                private readonly global::Microsoft.Extensions.Configuration.IConfiguration _configuration;

                internal AppSettingsShim(global::Microsoft.Extensions.Configuration.IConfiguration configuration)
                {
                    _configuration = configuration;
                }

                public string this[string key] => _configuration[key];

                public string Get(string key) => _configuration[key];
            }

            /// <summary>ConnectionStrings: the ConnectionStrings section.</summary>
            public sealed class ConnectionStringsShim
            {
                private readonly global::Microsoft.Extensions.Configuration.IConfiguration _configuration;

                internal ConnectionStringsShim(global::Microsoft.Extensions.Configuration.IConfiguration configuration)
                {
                    _configuration = configuration;
                }

                public ConnectionStringSettingsShim this[string name] =>
                    _configuration.GetSection("ConnectionStrings")[name] is string value ? new ConnectionStringSettingsShim(name, value) : null;
            }

            /// <summary>One connection string.</summary>
            public sealed class ConnectionStringSettingsShim
            {
                internal ConnectionStringSettingsShim(string name, string connectionString)
                {
                    Name = name;
                    ConnectionString = connectionString;
                }

                public string Name { get; }

                public string ConnectionString { get; }
            }
        }

        """;

    private static void Class(StringBuilder builder, SectionSchema schema, string summary, string? sectionKey, HashSet<string> written)
    {
        if (!written.Add(schema.OptionsName))
        {
            return;
        }

        builder.Append('\n');
        builder.Append(CultureInfo.InvariantCulture, $"    /// <summary>{Xml(summary)}</summary>\n");
        builder.Append(CultureInfo.InvariantCulture, $"    public class {schema.OptionsName}\n    {{\n");
        var members = new List<string>();
        if (sectionKey is not null)
        {
            members.Add(string.Create(CultureInfo.InvariantCulture, $"        public const string Section = \"{sectionKey}\";\n"));
        }

        foreach (var property in schema.Properties)
        {
            switch (property.Kind)
            {
                case SettingKind.Value:
                    members.Add(string.Create(CultureInfo.InvariantCulture, $"        public {property.TypeName} {property.Name} {{ get; set; }}{Initializer(property)}\n"));
                    break;
                case SettingKind.Element:
                    members.Add(string.Create(CultureInfo.InvariantCulture, $"        public {property.Element!.OptionsName} {property.Name} {{ get; set; }} = new {property.Element.OptionsName}();\n"));
                    break;
                default:
                    members.Add(string.Create(CultureInfo.InvariantCulture, $"        // {property.Name} ({property.XmlName}) is not converted: {property.Reason}\n"));
                    break;
            }
        }

        builder.Append(string.Join("\n", members));
        builder.Append("    }\n");
        foreach (var nested in schema.Properties.Where(p => p.Kind == SettingKind.Element))
        {
            Class(builder, nested.Element!, $"The {nested.Element!.TypeName} element.", null, written);
        }
    }

    /// <summary>The <c>DefaultValue</c> of <c>[ConfigurationProperty]</c> as an initializer; strings default to empty, as ConfigurationElement returns them.</summary>
    private static string Initializer(SettingProperty property)
    {
        var culture = CultureInfo.InvariantCulture;
        var value = property.DefaultValue;
        return (property.ValueType, value) switch
        {
            ("string", string text) => " = " + SymbolDisplay(text) + ";",
            ("string", null) => " = \"\";",
            ("bool", bool flag) => flag ? " = true;" : " = false;",
            ("bool", string text) when bool.TryParse(text, out var parsed) => parsed ? " = true;" : " = false;",
            ("int" or "long" or "short" or "byte" or "sbyte" or "ushort" or "uint" or "ulong", IConvertible number) when value is not string => " = " + System.Convert.ToString(number, culture) + ";",
            ("double" or "float", IConvertible number) when value is not string => " = " + System.Convert.ToDouble(number, culture).ToString("R", culture) + ";",
            ("TimeSpan", string text) when TimeSpan.TryParse(text, culture, out var span) =>
                " = global::System.TimeSpan.Parse(" + SymbolDisplay(span.ToString("c", culture)) + ", global::System.Globalization.CultureInfo.InvariantCulture);",
            ("Guid", string text) when Guid.TryParse(text, out var guid) => " = global::System.Guid.Parse(\"" + guid.ToString("D") + "\");",
            _ => "",
        };
    }

    private static string SymbolDisplay(string text) => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(text, quote: true);

    private static string Xml(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    private static void Header(StringBuilder builder, string what) => builder.Append(Header(what));

    private static string Header(string what) =>
        $"// <auto-generated>\n//     Generated by offramp config convert: {what}.\n// </auto-generated>\n\n";
}
