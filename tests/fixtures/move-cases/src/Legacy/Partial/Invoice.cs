using Legacy.Clean;

namespace Legacy.Partial
{
    /// <summary>(f) A partial class across two files: they move together.</summary>
    public sealed partial class Invoice
    {
        public Invoice(Money net) => Net = net;

        public Money Net { get; }
    }
}
