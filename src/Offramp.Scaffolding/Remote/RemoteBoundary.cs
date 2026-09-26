using System.Globalization;
using Microsoft.CodeAnalysis;
using Offramp.Analysis.Seams;
using Offramp.Scaffolding.Text;

namespace Offramp.Scaffolding.Remote;

/// <summary>An argument of a remote member: the parameter and the request property that carries it (null for a CancellationToken).</summary>
internal sealed record BoundaryArgument(IParameterSymbol Parameter, string? Property);

/// <summary>An interface member at the boundary.</summary>
internal sealed record BoundaryMember
{
    public required ISymbol Symbol { get; init; }

    /// <summary>The route and request name: the member name, numbered for overloads (<c>Find</c>, <c>Find2</c>).</summary>
    public required string Key { get; init; }

    /// <summary><c>remote</c>, <c>skipped</c>, or <c>blocked</c>.</summary>
    public required string Status { get; init; }

    public IReadOnlyList<string> Problems { get; init; } = [];

    public IMethodSymbol? Method => Symbol as IMethodSymbol;

    /// <summary>The member returns Task or ValueTask.</summary>
    public bool Async { get; init; }

    /// <summary>The type that crosses back, Task unwrapped; null for none.</summary>
    public ITypeSymbol? Returns { get; init; }

    public IReadOnlyList<BoundaryArgument> Arguments { get; init; } = [];

    /// <summary>The name of the client's asynchronous form of a synchronous member.</summary>
    public string AsyncName { get; init; } = "";
}

/// <summary>
/// The boundary audit of <c>remote</c> (docs/spec/commands/seams.md#remote): every interface
/// member must exchange data. Members that do not are blocked (OFR4002, an error here) unless
/// <c>--skip-member</c> leaves them out; properties, events, and generic methods do not cross.
/// </summary>
internal static class RemoteBoundary
{
    public static List<BoundaryMember> Audit(INamedTypeSymbol contract, IReadOnlyCollection<string> skip, WireTypes wire)
    {
        var members = contract.GetMembers().Concat(contract.AllInterfaces.SelectMany(i => i.GetMembers()))
            .Where(m => m is IMethodSymbol { MethodKind: MethodKind.Ordinary } or IPropertySymbol or IEventSymbol)
            .Where(m => !m.IsStatic)
            .ToList();
        var names = members.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var overloads = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<BoundaryMember>();
        foreach (var member in members)
        {
            var count = overloads[member.Name] = overloads.GetValueOrDefault(member.Name) + 1;
            var key = count == 1 ? member.Name : member.Name + count.ToString(CultureInfo.InvariantCulture);
            var problems = Problems(member, wire);
            var status = skip.Contains(member.Name, StringComparer.Ordinal) ? "skipped" : problems.Count > 0 ? "blocked" : "remote";
            if (member is not IMethodSymbol method)
            {
                result.Add(new BoundaryMember { Symbol = member, Key = key, Status = status, Problems = problems });
                continue;
            }

            var returns = WireTypes.Unwrap(method.ReturnType, out var async);
            var asyncName = method.Name + "Async";
            if (!async && names.Contains(asyncName))
            {
                asyncName = method.Name + "RemoteAsync";
            }

            result.Add(new BoundaryMember
            {
                Symbol = member,
                Key = key,
                Status = status,
                Problems = problems,
                Async = async,
                Returns = returns,
                Arguments = Arguments(method),
                AsyncName = async ? method.Name : asyncName,
            });
        }

        return result;
    }

    private static List<string> Problems(ISymbol member, WireTypes wire)
    {
        switch (member)
        {
            case IPropertySymbol:
                return ["properties do not cross the wire; use a method"];
            case IEventSymbol:
                return ["events cannot cross the wire"];
            case IMethodSymbol { IsGenericMethod: true }:
                return ["generic methods do not cross the wire"];
        }

        var method = (IMethodSymbol)member;
        var problems = WireFriendliness.Problems(method).ToList();
        if (problems.Count > 0)
        {
            return problems;
        }

        foreach (var parameter in method.Parameters.Where(p => !WireTypes.IsCancellationToken(p.Type)))
        {
            wire.Collect(parameter.Type, problems);
        }

        if (WireTypes.Unwrap(method.ReturnType, out _) is { } returns)
        {
            wire.Collect(returns, problems);
        }

        return problems;
    }

    /// <summary>Request properties: parameter names in PascalCase, numbered if two collide.</summary>
    private static List<BoundaryArgument> Arguments(IMethodSymbol method)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var arguments = new List<BoundaryArgument>();
        foreach (var parameter in method.Parameters)
        {
            if (WireTypes.IsCancellationToken(parameter.Type))
            {
                arguments.Add(new BoundaryArgument(parameter, null));
                continue;
            }

            var name = CSharpText.Pascal(parameter.Name);
            for (var i = 2; !taken.Add(name); i++)
            {
                name = CSharpText.Pascal(parameter.Name) + i.ToString(CultureInfo.InvariantCulture);
            }

            arguments.Add(new BoundaryArgument(parameter, name));
        }

        return arguments;
    }
}
