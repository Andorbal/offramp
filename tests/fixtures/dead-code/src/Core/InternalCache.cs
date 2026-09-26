using System.Collections.Generic;

namespace DeadCode.Core
{
    internal sealed class InternalCache
    {
        private readonly Dictionary<string, object> _items = new Dictionary<string, object>();

        public void Put(string key, object value)
        {
            _items[key] = value;
        }
    }
}
