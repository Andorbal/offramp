using System;

namespace DeadCode.Core
{
    /// <summary>Only the tests use it.</summary>
    public sealed class FixedClock
    {
        private readonly DateTime _now;

        public FixedClock(DateTime now)
        {
            _now = now;
        }

        public DateTime Now()
        {
            return _now;
        }
    }
}
