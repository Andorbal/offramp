using System;

namespace Foo.Shared
{
    public interface IClock
    {
        DateTime Today { get; }
    }

    /// <summary>Used by production code and by tests.</summary>
    public sealed class SystemClock : IClock
    {
        public DateTime Today => DateTime.Today;
    }
}
