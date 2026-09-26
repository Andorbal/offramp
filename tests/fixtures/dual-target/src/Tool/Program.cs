using Contracts;
using Shared;

var formatter = new Formatter();
Console.WriteLine(formatter.Format(new Order { Id = "A-1", Total = 12.5m }));
Console.WriteLine(formatter.Encode("a b") + " " + Clock.UtcNow().Year);
