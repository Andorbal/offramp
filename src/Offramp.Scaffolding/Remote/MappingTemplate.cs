using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Offramp.Scaffolding.Text;

namespace Offramp.Scaffolding.Remote;

/// <summary>
/// <c>{Stem}Mapping</c>: conversions between the original types and their contract copies. The
/// host and the client each compile their own copy (the contracts project cannot see the
/// original types).
/// </summary>
internal static class MappingTemplate
{
    public static string Text(RemoteLayout layout, WireTypes wire, string ns)
    {
        var body = new StringBuilder();
        foreach (var (type, dto) in wire.Dtos.Where(d => d.Type.TypeKind != TypeKind.Enum))
        {
            var original = CSharpText.Type(type);
            var copy = $"{wire.Namespace}.{dto}";
            var isClass = type.TypeKind == TypeKind.Class;
            var properties = WireTypes.Properties(type);
            body.Append(CultureInfo.InvariantCulture, $"        public static {copy} ToWire({original} value)\n        {{\n");
            if (isClass)
            {
                body.Append("            if (value == null)\n            {\n                return null;\n            }\n\n");
            }

            body.Append(CultureInfo.InvariantCulture, $"            return new {copy}\n            {{\n");
            foreach (var property in properties)
            {
                var name = CSharpText.Identifier(property.Name);
                body.Append(CultureInfo.InvariantCulture, $"                {name} = {wire.ToWire(property.Type, "value." + name)},\n");
            }

            body.Append("            };\n        }\n\n");
            if (!isClass)
            {
                body.Append(CultureInfo.InvariantCulture, $"        public static {copy} ToWire({original}? value)\n        {{\n            return value.HasValue ? ToWire(value.Value) : null;\n        }}\n\n");
            }

            body.Append(CultureInfo.InvariantCulture, $"        public static {original} FromWire({copy} value)\n        {{\n");
            body.Append(CultureInfo.InvariantCulture, $"            if (value == null)\n            {{\n                return {(isClass ? "null" : $"default({original})")};\n            }}\n\n");
            body.Append(CultureInfo.InvariantCulture, $"            return new {original}\n            {{\n");
            foreach (var property in properties)
            {
                var name = CSharpText.Identifier(property.Name);
                body.Append(CultureInfo.InvariantCulture, $"                {name} = {wire.FromWire(property.Type, "value." + name)},\n");
            }

            body.Append("            };\n        }\n\n");
            if (!isClass)
            {
                body.Append(CultureInfo.InvariantCulture, $"        public static {original}? FromWireNullable({copy} value)\n        {{\n            return value == null ? ({original}?)null : FromWire(value);\n        }}\n\n");
            }
        }

        return $$"""
            {{CSharpText.Header}}
            namespace {{ns}}
            {
                using System;
                using System.Collections.Generic;
                using System.Linq;

                /// <summary>Converts between the types of <c>{{layout.InterfaceFullName}}</c> and their copies in {{layout.ContractsNamespace}}.</summary>
                internal static class {{layout.Stem}}Mapping
                {
            {{body}}        public static List<TOut> MapList<TIn, TOut>(IEnumerable<TIn> source, Func<TIn, TOut> map)
                    {
                        return source == null ? null : source.Select(map).ToList();
                    }

                    public static TOut[] MapArray<TIn, TOut>(IEnumerable<TIn> source, Func<TIn, TOut> map)
                    {
                        return source == null ? null : source.Select(map).ToArray();
                    }

                    public static HashSet<TOut> MapSet<TIn, TOut>(IEnumerable<TIn> source, Func<TIn, TOut> map)
                    {
                        return source == null ? null : new HashSet<TOut>(source.Select(map));
                    }

                    public static Dictionary<string, TOut> MapDictionary<TIn, TOut>(IEnumerable<KeyValuePair<string, TIn>> source, Func<TIn, TOut> map)
                    {
                        return source == null ? null : source.ToDictionary(pair => pair.Key, pair => map(pair.Value));
                    }
                }
            }

            """;
    }
}
