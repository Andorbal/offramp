using System.Text.Json.Nodes;

namespace Offramp.Core.Diagnostics;

/// <summary>A severity override for one code, from <c>offramp.yml</c> <c>rules:</c>.</summary>
/// <param name="Severity">The new severity, or null to disable the code.</param>
/// <param name="Reason">Why; recorded so suppression is attributable.</param>
public sealed record SeverityOverride(Severity? Severity, string? Reason);

/// <summary>
/// Collects diagnostics for one command run. Thread-safe. Applies severity
/// overrides from configuration and always returns diagnostics in a stable order.
/// </summary>
public sealed class DiagnosticBag
{
    private readonly object _gate = new();
    private readonly List<Diagnostic> _items = [];
    private readonly IReadOnlyDictionary<string, SeverityOverride> _overrides;

    public DiagnosticBag(IReadOnlyDictionary<string, SeverityOverride>? overrides = null)
    {
        _overrides = overrides ?? new Dictionary<string, SeverityOverride>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Reports a diagnostic. Returns the stored diagnostic, or null when
    /// configuration disabled the code.
    /// </summary>
    public Diagnostic? Report(
        DiagnosticDescriptor descriptor,
        string message,
        DiagnosticLocation? location = null,
        IEnumerable<KeyValuePair<string, JsonNode?>>? data = null,
        Severity? severity = null)
    {
        var diagnostic = new Diagnostic
        {
            Code = descriptor.Code,
            Severity = severity ?? descriptor.DefaultSeverity,
            Message = message,
            Project = location?.Project,
            File = location?.File,
            Line = location?.Line,
            Column = location?.Column,
            Data = ToSorted(data),
            Help = descriptor.HelpUri,
        };

        return Add(diagnostic);
    }

    /// <summary>Adds an existing diagnostic (for example one produced before overrides were known).</summary>
    public Diagnostic? Add(Diagnostic diagnostic)
    {
        var effective = ApplyOverride(diagnostic);
        if (effective is null)
        {
            return null;
        }

        lock (_gate)
        {
            _items.Add(effective);
        }

        return effective;
    }

    public void AddRange(IEnumerable<Diagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            Add(diagnostic);
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public bool HasErrors => ToSortedList().Any(d => d.Severity == Severity.Error);

    public bool Contains(string code) => ToSortedList().Any(d => d.Code == code);

    /// <summary>Diagnostics ordered by code, project, file, line, column, then message.</summary>
    public IReadOnlyList<Diagnostic> ToSortedList()
    {
        lock (_gate)
        {
            return [.. _items.OrderBy(d => d, DiagnosticOrder.Instance)];
        }
    }

    public DiagnosticSummary Summary()
    {
        var list = ToSortedList();
        return new DiagnosticSummary(
            Errors: list.Count(d => d.Severity == Severity.Error),
            Warnings: list.Count(d => d.Severity == Severity.Warning),
            Info: list.Count(d => d.Severity == Severity.Info));
    }

    private Diagnostic? ApplyOverride(Diagnostic diagnostic)
    {
        if (!_overrides.TryGetValue(diagnostic.Code, out var rule))
        {
            return diagnostic;
        }

        if (rule.Severity is null)
        {
            return null;
        }

        return diagnostic with { Severity = rule.Severity.Value, Overridden = true };
    }

    private static SortedDictionary<string, JsonNode?> ToSorted(IEnumerable<KeyValuePair<string, JsonNode?>>? data)
    {
        var sorted = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
        if (data is null)
        {
            return sorted;
        }

        foreach (var (key, value) in data)
        {
            sorted[key] = value;
        }

        return sorted;
    }
}

/// <summary>The canonical diagnostic ordering used in every output.</summary>
public sealed class DiagnosticOrder : IComparer<Diagnostic>
{
    public static readonly DiagnosticOrder Instance = new();

    public int Compare(Diagnostic? x, Diagnostic? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var result = string.CompareOrdinal(x.Code, y.Code);
        if (result != 0) return result;
        result = string.CompareOrdinal(x.Project, y.Project);
        if (result != 0) return result;
        result = string.CompareOrdinal(x.File, y.File);
        if (result != 0) return result;
        result = Nullable.Compare(x.Line, y.Line);
        if (result != 0) return result;
        result = Nullable.Compare(x.Column, y.Column);
        if (result != 0) return result;
        return string.CompareOrdinal(x.Message, y.Message);
    }
}
