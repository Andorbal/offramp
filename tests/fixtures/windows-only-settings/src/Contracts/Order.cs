using System.Xml.Serialization;

namespace Contracts
{
    [XmlRoot("order")]
    public class Order
    {
        [XmlElement("customer")]
        public string Customer { get; set; }
    }
}
