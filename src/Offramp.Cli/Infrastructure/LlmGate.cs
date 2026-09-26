using System.Globalization;
using System.Text.Json.Nodes;
using Offramp.Core.Configuration;
using Offramp.Core.Diagnostics;
using Offramp.Core.Progress;
using Offramp.Llm;

namespace Offramp.Cli.Infrastructure;

/// <summary>
/// The only way a command reaches the model (docs/spec/commands/mcp-and-llm.md): a permitted
/// use, enabled in <c>llm.uses</c>, with <c>llm.enabled</c> or <c>--llm</c>. Every question
/// has a deterministic fallback; a failed call or an unusable answer is <c>OFR9001</c> and
/// the fallback is used.
/// </summary>
public sealed class LlmGate
{
    public const string Naming = "naming";
    public const string Ranking = "ranking";
    public const string Summarizing = "summarizing";
    public const string Classifying = "classifying";

    /// <summary>The value every LLM-influenced output field is marked with.</summary>
    public const string Source = "llm";

    private static readonly HttpClient Http = new();

    private readonly ILlm _llm;
    private readonly DiagnosticBag _diagnostics;

    private LlmGate(ILlm llm, DiagnosticBag diagnostics)
    {
        _llm = llm;
        _diagnostics = diagnostics;
    }

    /// <summary>The gate for a use, or null when the model may not be asked (the command uses its deterministic value).</summary>
    public static LlmGate? For(CommandContext context, string use)
    {
        var config = context.Config.Config.Llm;
        if (!config.Enabled || !config.Uses.Contains(use, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var llm = LlmFactory.Create(Settings(config, context.Host.Environment), context.Host.LlmHttp ?? Http, usage =>
        {
            if (context.Settings.Verbose)
            {
                context.Progress.Log(ProgressLevel.Debug, string.Create(CultureInfo.InvariantCulture,
                    $"llm {usage.Use}: {usage.Model}, {usage.InputTokens?.ToString(CultureInfo.InvariantCulture) ?? "?"} tokens in, {usage.OutputTokens?.ToString(CultureInfo.InvariantCulture) ?? "?"} out, {usage.Duration.TotalMilliseconds:0} ms"));
            }
        });
        return new LlmGate(llm, context.Diagnostics);
    }

    public static LlmSettings Settings(LlmConfig config, IReadOnlyDictionary<string, string> environment)
    {
        var anthropic = string.Equals(config.Provider, "anthropic", StringComparison.OrdinalIgnoreCase);
        var url = anthropic && config.Url == new LlmConfig().Url ? LlmFactory.AnthropicUrl : config.Url;
        return new LlmSettings
        {
            Provider = config.Provider,
            Url = url,
            Model = config.Model,
            ApiKey = environment.GetValueOrDefault(config.ApiKeyEnv),
        };
    }

    /// <summary>
    /// Asks for a JSON answer and turns it into a value with <paramref name="accept"/>; null (with
    /// OFR9001) when the call fails or <paramref name="accept"/> rejects the answer.
    /// </summary>
    public async Task<T?> AskAsync<T>(LlmRequest request, Func<JsonNode, T?> accept, string subject, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            var answer = await _llm.CompleteJsonAsync(request, cancellationToken);
            if (accept(answer) is { } value)
            {
                return value;
            }

            Unused(request.Use, subject, $"the answer {Clip(answer.ToJsonString())} is not usable");
        }
        catch (LlmException e)
        {
            Unused(request.Use, subject, e.Message);
        }
        catch (InvalidOperationException e)
        {
            Unused(request.Use, subject, "the answer is not in the expected shape: " + e.Message);
        }

        return null;
    }

    /// <summary>Free text; null (with OFR9001) when the call fails or the answer is empty.</summary>
    public async Task<string?> AskTextAsync(LlmRequest request, string subject, CancellationToken cancellationToken)
    {
        try
        {
            var answer = await _llm.CompleteAsync(request, cancellationToken);
            if (answer.Length > 0)
            {
                return answer;
            }

            Unused(request.Use, subject, "the answer is empty");
        }
        catch (LlmException e)
        {
            Unused(request.Use, subject, e.Message);
        }

        return null;
    }

    private void Unused(string use, string subject, string why) =>
        _diagnostics.Report(DiagnosticCatalog.OFR9001, $"{use} for {subject}: {why}; the deterministic value is used.",
            data: [KeyValuePair.Create<string, JsonNode?>("use", use)]);

    private static string Clip(string text) => text.Length <= 120 ? text : text[..120] + "…";
}
