using System.Web.Script.Serialization;

namespace Shop
{
    public class Json
    {
        public string Write(object value)
        {
            var serializer = new JavaScriptSerializer();
            return serializer.Serialize(value);
        }

        public T Read<T>(string json)
        {
            return new JavaScriptSerializer().Deserialize<T>(json);
        }
    }
}
