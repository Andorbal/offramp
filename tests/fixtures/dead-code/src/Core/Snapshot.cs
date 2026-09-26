using System;

namespace DeadCode.Core
{
    /// <summary>Serialized by a job in another repository.</summary>
    [Serializable]
    public sealed class Snapshot
    {
        public DateTime TakenAt;

        public decimal Total;
    }
}
