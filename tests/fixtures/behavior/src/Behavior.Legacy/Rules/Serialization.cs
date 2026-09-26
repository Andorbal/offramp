using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Web.Script.Serialization;
using System.Xml.Serialization;

// audit serialization: each class is named after its rule.
namespace Behavior.Rules
{
    internal static class OFR3201
    {
        public static object Positive()
        {
            return new BinaryFormatter();
        }

        public static object Negative()
        {
            return new DataContractSerializer(typeof(int));
        }
    }

    /// <summary>The deep-clone idiom: the data never leaves the member.</summary>
    internal static class OFR3202
    {
        public static T Positive<T>(T value)
        {
            using (var buffer = new MemoryStream())
            {
                var formatter = new BinaryFormatter();
                formatter.Serialize(buffer, value);
                buffer.Position = 0;
                return (T)formatter.Deserialize(buffer);
            }
        }

        public static void Negative(Invoice invoice, string path)
        {
            using (var file = File.Create(path))
            {
                new BinaryFormatter().Serialize(file, invoice);
            }
        }
    }

    /// <summary>Persistence: to a file, and bytes returned to the caller.</summary>
    internal static class OFR3203
    {
        public static void Positive(Invoice invoice, string path)
        {
            using (var file = new FileStream(path, FileMode.Create))
            {
                new BinaryFormatter().Serialize(file, invoice);
            }
        }

        public static byte[] PositiveBytes(Customer customer)
        {
            var buffer = new MemoryStream();
            new BinaryFormatter().Serialize(buffer, customer);
            return buffer.ToArray();
        }

        public static Invoice PositiveRead(Stream stream)
        {
            return (Invoice)new BinaryFormatter().Deserialize(stream);
        }

        public static Invoice Negative(Invoice invoice)
        {
            return OFR3202.Positive(invoice);
        }
    }

    internal static class OFR3204
    {
        public static void Positive(Stream stream)
        {
            new BinaryFormatter().Serialize(stream, new Payload());
        }

        public static void Negative(Stream stream)
        {
            new XmlSerializer(typeof(Receipt)).Serialize(stream, new Receipt());
        }
    }

    internal static class OFR3205
    {
        [Serializable]
        public sealed class Positive
        {
            public string Name;
        }

        [Serializable]
        public sealed class Negative
        {
            public Line[] Lines;
        }
    }

    internal static class OFR3210
    {
        public static string Positive(object value)
        {
            return new JavaScriptSerializer().Serialize(value);
        }

        public static object Negative()
        {
            return new DataContractSerializer(typeof(Receipt));
        }
    }

    internal static class OFR3211
    {
        public static object Positive()
        {
            return new XmlSerializer(typeof(Receipt));
        }

        public static object Negative()
        {
            return new DataContractSerializer(typeof(Receipt));
        }
    }

    [Serializable]
    internal sealed class Invoice
    {
        public OFR3205.Negative Body;

        [NonSerialized]
        public Action Changed;
    }

    [Serializable]
    internal sealed class Customer : ISerializable
    {
        public Customer()
        {
        }

        private Customer(SerializationInfo info, StreamingContext context)
        {
        }

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
        }
    }

    [Serializable]
    internal sealed class Payload
    {
        public Func<int> Compute;

        [OnDeserialized]
        private void Restored(StreamingContext context)
        {
        }
    }

    [Serializable]
    internal sealed class Line
    {
        public decimal Amount;
    }

    internal sealed class Receipt
    {
        public string Number;
    }
}
