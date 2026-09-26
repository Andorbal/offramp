namespace Legacy.Internal
{
    /// <summary>(h) Internal, and used by code that stays in Legacy.</summary>
    internal static class Rounding
    {
        internal static decimal ToCents(decimal value) => decimal.Round(value, 2);
    }
}
