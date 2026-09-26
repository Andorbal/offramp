using System;
using System.Collections.Generic;

namespace Heartbeat
{
    /// <summary>The beats so far: plain code the worker keeps.</summary>
    public sealed class Pulse
    {
        private readonly List<DateTime> _beats = new List<DateTime>();

        public string Source { get; private set; }

        public int Count => _beats.Count;

        public DateTime? Last => _beats.Count == 0 ? (DateTime?)null : _beats[_beats.Count - 1];

        public void Reset(string source)
        {
            Source = source;
            _beats.Clear();
        }

        public void Beat(DateTime at)
        {
            _beats.Add(at);
        }

        public void Flush()
        {
            Console.WriteLine(Source + ": " + Count + " beats");
        }
    }
}
