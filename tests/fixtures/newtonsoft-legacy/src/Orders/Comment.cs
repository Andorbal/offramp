using System.Collections.Generic;
using Newtonsoft.Json;

namespace Contoso.Orders
{
    /// <summary>Threads of replies arrive from outside and are only read; they can nest deeply.</summary>
    public class Comment
    {
        public string Text { get; set; }
        public List<Comment> Replies { get; set; }
    }

    public static class CommentCodec
    {
        public static Comment Read(string json)
        {
            return JsonConvert.DeserializeObject<Comment>(json);
        }
    }
}
