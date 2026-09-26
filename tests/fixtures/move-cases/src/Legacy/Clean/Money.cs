namespace Legacy.Clean
{
    /// <summary>(a) Depends on nothing: moves cleanly.</summary>
    public readonly struct Money
    {
        public Money(decimal amount) => Amount = amount;

        public decimal Amount { get; }

        public Money Add(Money other) => new Money(Amount + other.Amount);
    }
}
