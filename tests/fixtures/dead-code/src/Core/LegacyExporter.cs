using System.Collections.Generic;
using System.Text;

namespace DeadCode.Core
{
    /// <summary>Exported orders to the old mainframe format.</summary>
    public sealed class LegacyExporter
    {
        public string Export(IEnumerable<decimal> totals)
        {
            var text = new StringBuilder();
            foreach (var total in totals)
            {
                text.AppendLine(total.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            }

            return text.ToString();
        }
    }
}
