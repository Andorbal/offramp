using System.IO;
using System.Runtime.Serialization.Formatters.Binary;

namespace Shared
{
    public static class Cloner
    {
        public static T Clone<T>(T source)
        {
            var formatter = new BinaryFormatter();
            using (var stream = new MemoryStream())
            {
                formatter.Serialize(stream, source);
                stream.Position = 0;
                return (T)formatter.Deserialize(stream);
            }
        }
    }
}
