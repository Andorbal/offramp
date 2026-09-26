using System.Globalization;
using System.Text;

namespace Offramp.Scaffolding.Web;

/// <summary><c>web inventory --format markdown</c>: the inventory as a document to plan the migration with.</summary>
public static class WebInventoryMarkdown
{
    public static string Write(WebInventoryResult result)
    {
        var culture = CultureInfo.InvariantCulture;
        var builder = new StringBuilder();
        builder.Append(culture, $"# Web inventory: {result.Project}\n\n");
        builder.Append(culture, $"Kind: {result.Kind}. URL: {result.Url ?? "unknown"}.\n\n");

        builder.Append("## Controllers\n\n| Controller | Kind | Action | Methods | Routes | Filters |\n|---|---|---|---|---|---|\n");
        foreach (var controller in result.Controllers)
        {
            foreach (var action in controller.Actions)
            {
                builder.Append(culture, $"| {controller.Type} | {controller.Kind} | {action.Name} | {Join(action.HttpMethods, "any")} | {Join(action.Routes, "convention")} | {Join(controller.Filters.Concat(action.Filters), "")} |\n");
            }
        }

        builder.Append("\n## Convention routes\n\n| Name | Template | Kind | Defaults |\n|---|---|---|---|\n");
        foreach (var route in result.Routes)
        {
            builder.Append(culture, $"| {route.Name} | `{route.Template}` | {route.Kind}{(route.Area is null ? "" : $" ({route.Area})")} | {Join(route.Defaults, "")} |\n");
        }

        builder.Append(culture, $"\nAttribute routing: {Join(result.AttributeRouting, "off")}. Global filters: {Join(result.GlobalFilters, "none")}. Areas: {Join(result.Areas, "none")}.\n");
        builder.Append("\n## Modules and handlers\n\n");
        foreach (var module in result.Modules)
        {
            builder.Append(culture, $"- module {module.Name}: {module.Type}{(module.File is null ? "" : $" ({module.File})")}\n");
        }

        foreach (var handler in result.Handlers)
        {
            builder.Append(culture, $"- handler {handler.Path ?? handler.Name} [{handler.Verb ?? "*"}]: {handler.Type}\n");
        }

        builder.Append(culture, $"\n## Application\n\n- Global.asax: {Join(result.GlobalAsax, "none")}\n- Web Forms: {Join(result.WebForms, "none")}\n");
        builder.Append(culture, $"- Session: {result.Session.Count} site{(result.Session.Count == 1 ? "" : "s")}\n- Output cache: {Join(result.OutputCache.Select(o => o.What), "none")}\n");
        var settings = result.Settings;
        builder.Append(culture, $"- Authentication: {settings.Authentication ?? "none"}{(settings.LoginUrl is null ? "" : $" ({settings.LoginUrl})")}; session state: {settings.SessionState ?? "default"}; machine key: {(settings.MachineKey ? "set" : "no")}; custom errors: {settings.CustomErrors ?? "default"}\n");
        builder.Append("\n## System.Web API surface\n\n");
        foreach (var file in result.SystemWeb)
        {
            builder.Append(culture, $"- {file.File}: {string.Join(", ", file.Types)}\n");
        }

        return builder.ToString();
    }

    private static string Join(IEnumerable<string> values, string empty)
    {
        var list = values.ToList();
        return list.Count == 0 ? empty : string.Join(", ", list);
    }
}
