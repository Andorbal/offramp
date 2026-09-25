namespace Offramp.Fixtures;

/// <summary>A clock that only moves when told to.</summary>
public sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    private long _timestamp;

    public static readonly DateTimeOffset DefaultStart = new(2026, 9, 25, 20, 11, 4, TimeSpan.Zero);

    public FakeTimeProvider()
        : this(DefaultStart)
    {
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _timestamp;

    public void Advance(TimeSpan by)
    {
        _now += by;
        _timestamp += by.Ticks;
    }
}
