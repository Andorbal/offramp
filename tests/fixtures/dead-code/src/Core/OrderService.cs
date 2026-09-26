namespace DeadCode.Core
{
    public sealed class OrderService
    {
        public decimal Total(decimal net, decimal taxRate)
        {
            return Round(net * (1 + taxRate));
        }

        /// <summary>Nobody archives orders any more.</summary>
        public void Archive(int orderId)
        {
            System.Console.WriteLine("archived " + orderId);
        }

        private static decimal Round(decimal value)
        {
            return System.Math.Round(value, 2);
        }
    }
}
