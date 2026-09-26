namespace DeadCode.Core
{
    public interface IHandler
    {
        void Handle(string message);
    }

    /// <summary>Registered by assembly scanning, never named in code.</summary>
    public sealed class InvoiceHandler : IHandler
    {
        public void Handle(string message)
        {
            System.Console.WriteLine("invoice " + message);
        }
    }
}
