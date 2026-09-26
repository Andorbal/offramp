namespace Legacy.Internal
{
    /// <summary>Stays in Legacy and keeps using the internal Rounding after it moves.</summary>
    public static class Billing
    {
        public static decimal Charge(decimal value) => Rounding.ToCents(value);
    }
}
