namespace Evidence.Client
{
    public sealed class ShopClient
    {
        public string Endpoint
        {
            get { return "https://shop.example"; }
        }

        /// <summary>No code in the repository calls it; the package's users may.</summary>
        public void Reset()
        {
            System.Console.WriteLine("reset");
        }
    }
}
