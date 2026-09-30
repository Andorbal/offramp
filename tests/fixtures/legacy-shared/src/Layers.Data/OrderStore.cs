using System.Collections.Generic;
using Contoso.Layers.Domain;

namespace Contoso.Layers.Data
{
    public class OrderStore
    {
        private readonly List<Order> _orders = new List<Order>();

        public void Add(Order order)
        {
            _orders.Add(order);
        }

        public int Count
        {
            get { return _orders.Count; }
        }
    }
}
