using System.IO;
using System.Xml.Serialization;
using Contracts;

namespace Legacy
{
    public static class CustomerNames
    {
        public static string Format(Order order)
        {
            var writer = new StringWriter();
            new XmlSerializer(typeof(Order)).Serialize(writer, order);
            return writer.ToString();
        }
    }
}
