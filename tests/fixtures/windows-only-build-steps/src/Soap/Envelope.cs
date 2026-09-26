using System.Xml.Serialization;

namespace Soap
{
    [XmlRoot("envelope")]
    public sealed class Envelope
    {
        [XmlElement("body")]
        public string Body { get; set; }
    }
}
