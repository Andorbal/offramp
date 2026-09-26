using System;

namespace Shared
{
    public static class Clock
    {
        public static DateTime Today() => DateTime.UtcNow.Date;
    }
}
