namespace RoslynPad.Core;

public sealed class RewriteRequest
{
    public RewriteRequest(IReadOnlyList<string> rewriterPaths, IReadOnlyList<string>? typeNames = null)
    {
        if (rewriterPaths.Count == 0)
            throw new ArgumentException("At least one rewriter path must be supplied.", nameof(rewriterPaths));

        RewriterPaths = rewriterPaths;
        TypeNames = typeNames ?? [];
    }

    public IReadOnlyList<string> RewriterPaths { get; }

    /// <summary>When non-empty, fully defines the pipeline order; otherwise declaration order is used.</summary>
    public IReadOnlyList<string> TypeNames { get; }
}
