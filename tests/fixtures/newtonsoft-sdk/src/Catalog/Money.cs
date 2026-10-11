using System.Runtime.Serialization;
using Newtonsoft.Json;

namespace Contoso.Catalog
{
    /// <summary>ISerializable without [Serializable].</summary>
    public sealed class Money : ISerializable
    {
        public Money(decimal amount, string currency)
        {
            Amount = amount;
            Currency = currency;
        }

        private Money(SerializationInfo info, StreamingContext context)
        {
            Amount = info.GetDecimal("amount");
            Currency = info.GetString("currency");
        }

        public decimal Amount { get; }

        public string Currency { get; }

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("amount", Amount);
            info.AddValue("currency", Currency);
        }
    }

    public static class MoneyCodec
    {
        public static string Write(Money money) => JsonConvert.SerializeObject(money);

        public static Money Read(string json) => JsonConvert.DeserializeObject<Money>(json);
    }
}
