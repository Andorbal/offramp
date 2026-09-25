using System.Text.Json.Serialization;

namespace Offramp.Core.Progress;

/// <summary>NDJSON progress events, one JSON object per line on stderr.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "event")]
[JsonDerivedType(typeof(PhaseEvent), "phase")]
[JsonDerivedType(typeof(ProgressEvent), "progress")]
[JsonDerivedType(typeof(LogEvent), "log")]
[JsonDerivedType(typeof(DoneEvent), "done")]
public abstract record ProgressEventBase;

public sealed record PhaseEvent(string Name, int Index, int Of) : ProgressEventBase;

public sealed record ProgressEvent(string Phase, int Current, int Total, string? Item) : ProgressEventBase;

public sealed record LogEvent(ProgressLevel Level, string Message) : ProgressEventBase;

public sealed record DoneEvent(string Phase, long DurationMs) : ProgressEventBase;
