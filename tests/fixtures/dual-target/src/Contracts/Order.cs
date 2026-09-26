namespace Contracts;

public sealed class Order
{
    public string Id { get; set; } = "";

    public decimal Total { get; set; }
}

public interface IFormatter
{
    string Format(Order order);
}
