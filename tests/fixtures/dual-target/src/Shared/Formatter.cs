using Contracts;
using Newtonsoft.Json;

namespace Shared;

public sealed class Formatter : IFormatter
{
    public string Format(Order order) => JsonConvert.SerializeObject(order);

#if NETFRAMEWORK
    public string Encode(string value) => System.Web.HttpUtility.UrlEncode(value);
#else
    public string Encode(string value) => System.Net.WebUtility.UrlEncode(value);
#endif
}
