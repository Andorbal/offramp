using Legacy.Clean;

namespace Legacy.Partial
{
    public sealed partial class Invoice
    {
        public Money Gross => new Money(Net.Amount * 1.2m);
    }
}
