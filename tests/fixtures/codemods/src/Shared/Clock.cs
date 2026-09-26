using System;

namespace Shared
{
    public static class Clock
    {
        public static DateTime PacificNow()
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
        }
    }
}
