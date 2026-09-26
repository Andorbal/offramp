using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Offramp.Core.Diagnostics;
using ProjectInfo = Offramp.Core.Model.ProjectInfo;
using Offramp.Fixtures;
using Offramp.Scaffolding.Remote;

namespace Offramp.Scaffolding.Tests;

/// <summary>
/// <c>remote</c> on in-memory projects: the boundary audit, the DTOs, and generated client code
/// that compiles for every shape of type that can cross the wire.
/// </summary>
public sealed class RemoteGeneratorTests
{
    private const string Shapes = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;

        namespace Shop.Orders
        {
            public enum Status { Open = 1, Shipped = 2 }

            [Flags]
            public enum Tags : long { None = 0, Rush = 1, Gift = 4 }

            public struct Money
            {
                public decimal Amount { get; set; }
                public string Currency { get; set; }
            }

            public class Line
            {
                public string Sku { get; set; }
                public int Quantity { get; set; }
                public Money Price { get; set; }
            }

            public class Entity
            {
                public Guid Id { get; set; }
            }

            public class Order : Entity
            {
                public Status Status { get; set; }
                public Status? Previous { get; set; }
                public Tags Tags { get; set; }
                public Line[] Lines { get; set; }
                public List<Line> Extra { get; set; }
                public HashSet<string> Labels { get; set; }
                public Dictionary<string, Money> Totals { get; set; }
                public IReadOnlyList<DateTimeOffset> History { get; set; }
                public Money? Discount { get; set; }
            }

            public interface IOrderStore
            {
                Order Find(Guid id);
                Order Find(string number, Status? status);
                Task<IReadOnlyList<Order>> SearchAsync(Status status, CancellationToken cancellationToken);
                Task SaveAsync(Order order);
                void Touch(Line[] lines, IDictionary<string, int> counts);
                int Count { get; }
            }

            public sealed class OrderStore : IOrderStore
            {
                public Order Find(Guid id) => null;
                public Order Find(string number, Status? status) => null;
                public Task<IReadOnlyList<Order>> SearchAsync(Status status, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Order>>(new List<Order>());
                public Task SaveAsync(Order order) => Task.CompletedTask;
                public void Touch(Line[] lines, IDictionary<string, int> counts) { }
                public int Count => 0;
            }
        }
        """;

    [Fact]
    [ProducesDiagnostic("OFR4002")]
    public async Task Members_that_cannot_cross_block_generation_until_skipped()
    {
        var bag = new DiagnosticBag();

        var plan = await RemoteGenerator.PlanAsync(Request("Shop.Orders.IOrderStore", bag), Compile(Shapes), CancellationToken.None);

        Assert.NotNull(plan);
        Assert.Null(plan.ChangeSet);
        var count = Assert.Single(plan.Result.Members, m => m.Name == "Count");
        Assert.Equal(("blocked", "properties do not cross the wire; use a method"), (count.Status, count.Problems[0]));
        var error = Assert.Single(bag.ToSortedList(), d => d.Code == "OFR4002");
        Assert.Equal(Severity.Error, error.Severity);
    }

    [Fact]
    [ProducesDiagnostic("OFR4020")]
    public async Task Every_shape_maps_both_ways_and_the_client_compiles()
    {
        var bag = new DiagnosticBag();
        var compilation = Compile(Shapes);

        var plan = await RemoteGenerator.PlanAsync(Request("Shop.Orders.IOrderStore", bag) with { SkipMembers = ["Count"], AsyncVariant = true }, compilation, CancellationToken.None);

        Assert.NotNull(plan?.ChangeSet);
        var result = plan.Result;
        Assert.Equal(
            ["/IOrderStore/Find", "/IOrderStore/Find2", "/IOrderStore/SearchAsync", "/IOrderStore/SaveAsync", "/IOrderStore/Touch", null],
            result.Members.Select(m => m.Route));
        Assert.Equal(
            ["Shop.Orders.Line", "Shop.Orders.Money", "Shop.Orders.Order", "Shop.Orders.Status", "Shop.Orders.Tags"],
            result.Dtos.Select(d => d.Type));
        Assert.Equal("net48", result.HostFramework);
        Assert.Equal(["Find", "Find", "Touch"], bag.ToSortedList().Where(d => d.Code == "OFR4020").Select(d => d.Data["member"]!.GetValue<string>()));

        var files = plan.ChangeSet.Creates.ToDictionary(c => c.Path, c => Encoding.UTF8.GetString(c.Content));
        Assert.Contains("    [System.Flags]\n    public enum TagsDto : long\n", files["src/Shop.Remote.Contracts/OrderStoreDtos.cs"], StringComparison.Ordinal);
        Assert.Contains("public System.Collections.Generic.List<Shop.Remote.Contracts.LineDto> Lines { get; set; }", files["src/Shop.Remote.Contracts/OrderStoreDtos.cs"], StringComparison.Ordinal);
        Assert.Contains("public Shop.Remote.Contracts.StatusDto? Status { get; set; }", files["src/Shop.Remote.Contracts/OrderStoreRequests.cs"], StringComparison.Ordinal);

        // The contracts and the client (all but the DI registration) compile with the original types.
        var client = files.Where(f => f.Key.StartsWith("src/Shop.Remote.Contracts/", StringComparison.Ordinal) && f.Key.EndsWith(".cs", StringComparison.Ordinal)
                || f.Key.StartsWith("src/Shop.Remote/", StringComparison.Ordinal) && f.Key.EndsWith(".cs", StringComparison.Ordinal) && !f.Key.EndsWith("Registration.cs", StringComparison.Ordinal))
            .Select(f => CSharpSyntaxTree.ParseText(f.Value, path: f.Key))
            .Append(CSharpSyntaxTree.ParseText(Shapes))
            .ToList();
        Assert.Equal(9, client.Count);
        var errors = CSharpCompilation.Create("Client", client, References(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToList();
        Assert.Empty(errors);
    }

    [Fact]
    public async Task Generated_text_is_deterministic()
    {
        var first = await RemoteGenerator.PlanAsync(Request("Shop.Orders.IOrderStore", new DiagnosticBag()) with { SkipMembers = ["Count"] }, Compile(Shapes), CancellationToken.None);
        var second = await RemoteGenerator.PlanAsync(Request("Shop.Orders.IOrderStore", new DiagnosticBag()) with { SkipMembers = ["Count"] }, Compile(Shapes), CancellationToken.None);

        Assert.Equal(first!.Result.Preview, second!.Result.Preview);
        Assert.Equal(first.ChangeSet!.Creates.Select(c => c.Path), second.ChangeSet!.Creates.Select(c => c.Path));
    }

    [Fact]
    public async Task Generic_types_and_classes_without_a_parameterless_constructor_do_not_cross()
    {
        const string source = """
            namespace Shop
            {
                public class Page<T> { public T[] Items { get; set; } }
                public class Sealed { public Sealed(int id) { Id = id; } public int Id { get; set; } }
                public interface IPages { Page<int> First(); Sealed Get(); }
                public class Pages : IPages { public Page<int> First() => null; public Sealed Get() => null; }
            }
            """;
        var bag = new DiagnosticBag();

        var plan = await RemoteGenerator.PlanAsync(Request("Shop.IPages", bag), Compile(source), CancellationToken.None);

        Assert.Equal(
            ["Shop.Page<int> is generic; DTOs are generated for non-generic types only", "Shop.Sealed has no public parameterless constructor"],
            plan!.Result.Members.SelectMany(m => m.Problems));
    }

    [Fact]
    [ProducesDiagnostic("OFR4023")]
    public async Task An_unknown_interface_member_or_ambiguous_implementation_is_reported()
    {
        const string source = "namespace S { public interface IA { int Get(); } public class A1 : IA { public int Get() => 1; } public class A2 : IA { public int Get() => 2; } }";
        var missing = new DiagnosticBag();
        var ambiguous = new DiagnosticBag();
        var skip = new DiagnosticBag();

        Assert.Null(await RemoteGenerator.PlanAsync(Request("S.IMissing", missing), Compile(source), CancellationToken.None));
        Assert.Null(await RemoteGenerator.PlanAsync(Request("S.IA", ambiguous), Compile(source), CancellationToken.None));
        Assert.Null(await RemoteGenerator.PlanAsync(Request("S.IA", skip) with { Implementation = "S.A1", SkipMembers = ["Nope"] }, Compile(source), CancellationToken.None));

        Assert.Contains("'S.IMissing' is not an interface", Assert.Single(missing.ToSortedList()).Message, StringComparison.Ordinal);
        Assert.Equal("S.A1, S.A2 implement IA; pass --implementation.", Assert.Single(ambiguous.ToSortedList()).Message);
        Assert.Equal("--skip-member Nope: IA has no member named Nope.", Assert.Single(skip.ToSortedList()).Message);
    }

    [Fact]
    [ProducesDiagnostic("OFR4022")]
    public async Task Without_a_trial_compilation_auto_falls_back_to_net48()
    {
        var bag = new DiagnosticBag();

        var plan = await RemoteGenerator.PlanAsync(Request("Shop.Orders.IOrderStore", bag) with { HostFramework = "auto", SkipMembers = ["Count"] }, Compile(Shapes), CancellationToken.None);

        Assert.Equal("net48", plan!.Result.HostFramework);
        Assert.Contains(bag.ToSortedList(), d => d.Code == "OFR4022" && d.Message == "The host is the net48 fallback: no reference resolver.");
        Assert.Contains("src/Shop.Windows.Host/OrderStoreController.cs", plan.Result.Files);
    }

    private static RemoteRequest Request(string contract, DiagnosticBag bag) => new()
    {
        RepositoryRoot = Path.Combine(Path.GetTempPath(), "offramp-remote-tests-" + Guid.NewGuid().ToString("N")),
        Project = new ProjectInfo { Id = "src/Shop/Shop.csproj", Name = "Shop", TargetFrameworks = ["net48"] },
        Interface = contract,
        HostFramework = "net48",
        Diagnostics = bag,
    };

    private static CSharpCompilation Compile(string source) =>
        CSharpCompilation.Create("Shop", [CSharpSyntaxTree.ParseText(source, path: "src/Shop/Orders.cs")], References(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    private static List<MetadataReference> References() =>
        [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))];
}
