using System;
using Foo.Shared;

namespace Foo.Testing
{
    /// <summary>Referenced from nowhere: looks like test support, but nothing proves it.</summary>
    public sealed class FakeClock : IClock
    {
        public DateTime Today { get; set; } = new DateTime(2020, 1, 1);
    }
}
