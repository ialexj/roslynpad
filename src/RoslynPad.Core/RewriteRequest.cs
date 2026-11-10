namespace RoslynPad.Core;

public sealed class RewriteRequest
{
    public RewriteRequest(
        IReadOnlyList<string> rewriterPaths,
        IReadOnlyList<string>? typeNames = null,
        bool repeatUntilStable = false,
        int maxIterations = 10,
        bool emitDebugSymbols = false)
    {
        if (rewriterPaths.Count == 0)
            throw new ArgumentException("At least one rewriter path must be supplied.", nameof(rewriterPaths));
        if (maxIterations < 1)
            throw new ArgumentException("Max iterations must be at least 1.", nameof(maxIterations));

        RewriterPaths = rewriterPaths;
        TypeNames = typeNames ?? [];
        RepeatUntilStable = repeatUntilStable;
        MaxIterations = maxIterations;
        EmitDebugSymbols = emitDebugSymbols;
    }

    public IReadOnlyList<string> RewriterPaths { get; }

    /// <summary>When non-empty, fully defines the pipeline order; otherwise declaration order is used.</summary>
    public IReadOnlyList<string> TypeNames { get; }

    /// <summary>Re-runs the full pipeline against the previous round's in-memory result until a round changes nothing.</summary>
    public bool RepeatUntilStable { get; }

    /// <summary>Caps repeat-until-stable rounds; ignored when RepeatUntilStable is false.</summary>
    public int MaxIterations { get; }

    /// <summary>
    /// When true, source-compiled rewriters are emitted with a portable PDB so a debugger
    /// attached to the host process can bind source-level breakpoints inside the rewriter.
    /// Precompiled .dll rewriters always load whatever symbols sit next to them.
    /// </summary>
    public bool EmitDebugSymbols { get; }
}
