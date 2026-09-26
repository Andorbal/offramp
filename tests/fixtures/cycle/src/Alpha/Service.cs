namespace Alpha
{
    public sealed class Service
    {
        public string Describe() => new Beta.Repository().Name;
    }
}
