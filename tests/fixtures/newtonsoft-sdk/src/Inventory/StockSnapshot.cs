using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Contoso.Inventory
{
    public interface IClock
    {
        DateTime UtcNow { get; }
    }

    public class StockSnapshot
    {
        public DateTime TakenAt { get; set; }
        public Dictionary<string, int> Levels { get; set; }
    }

    /// <summary>Needs an interface in its constructor: no probe can create it.</summary>
    public sealed class StockSnapshotWriter
    {
        private readonly IClock _clock;

        public StockSnapshotWriter(IClock clock)
        {
            _clock = clock;
        }

        public string Write(IDictionary<string, int> levels) =>
            JsonConvert.SerializeObject(new StockSnapshot { TakenAt = _clock.UtcNow, Levels = new Dictionary<string, int>(levels) });
    }

    public static class StockSnapshotReader
    {
        public static StockSnapshot Read(string json) => JsonConvert.DeserializeObject<StockSnapshot>(json);
    }
}
