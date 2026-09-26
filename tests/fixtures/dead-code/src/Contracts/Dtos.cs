namespace DeadCode.Contracts
{
    public sealed class CustomerDto
    {
        public string Name { get; set; }
    }

    /// <summary>Public in a packable library: another repository may still use it.</summary>
    public sealed class LegacyDto
    {
        public int Id { get; set; }
    }
}
