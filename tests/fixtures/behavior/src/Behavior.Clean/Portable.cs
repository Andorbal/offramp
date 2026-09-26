using System;
using System.Collections.Generic;
using System.Linq;

namespace Behavior.Clean
{
    /// <summary>Code every rule leaves alone: the negative project.</summary>
    public static class Portable
    {
        public static int Count(IEnumerable<string> names)
        {
            return names.Count(n => n.StartsWith("a", StringComparison.Ordinal));
        }

        public static string Describe(double value)
        {
            return value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
