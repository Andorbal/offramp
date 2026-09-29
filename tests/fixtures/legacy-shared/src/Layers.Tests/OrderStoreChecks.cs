using Contoso.Layers.Data;

namespace Contoso.Layers.Tests
{
    public static class OrderStoreChecks
    {
        public static bool StartsEmpty()
        {
            return new OrderStore().Count == 0;
        }
    }
}
