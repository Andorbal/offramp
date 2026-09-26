using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Offramp.Analyzers;

/// <summary>The analyzer half of a codemod: reports each site, with a skip reason when it is not rewritten.</summary>
public abstract class CodemodAnalyzer : DiagnosticAnalyzer
{
    public abstract Codemod Codemod { get; }

    public sealed override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Codemod.Descriptor);

    public sealed override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        Register(context);
    }

    protected abstract void Register(AnalysisContext context);
}
