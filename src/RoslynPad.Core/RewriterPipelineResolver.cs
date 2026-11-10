namespace RoslynPad.Core;

internal sealed class RewriterPipelineResolver(RewriterLoader loader)
{
    internal IReadOnlyList<Type> Resolve(IReadOnlyList<string> rewriterPaths, IReadOnlyList<string> typeNames, bool emitDebugSymbols = false)
    {
        // Load each unique path once, preserving first-occurrence order.
        var sourceByPath = new Dictionary<string, RewriterSource>(StringComparer.OrdinalIgnoreCase);
        var orderedSources = new List<RewriterSource>();
        foreach (var path in rewriterPaths)
        {
            if (!sourceByPath.TryGetValue(path, out var source))
            {
                source = loader.Load(path, emitDebugSymbols);
                sourceByPath[path] = source;
                orderedSources.Add(source);
            }
        }

        var anyDll = rewriterPaths.Any(RewriterLoader.IsDll);

        if (typeNames.Count == 0)
        {
            if (anyDll)
                throw new InvalidOperationException("--type is required when any --rewriter is a precompiled assembly (.dll).");

            // Default mode: expand each --rewriter occurrence (with repeats) into its source's types in declaration order.
            // Nested rewriter classes are excluded — they're typically helpers, not standalone pipeline stages.
            // An explicit --type list is not subject to this rule and can still name a nested rewriter.
            var pipeline = new List<Type>();
            foreach (var path in rewriterPaths)
            {
                var source = sourceByPath[path];
                pipeline.AddRange(source.Types.Where(t => !t.IsNested));
            }
            return pipeline;
        }
        else
        {
            // Explicit mode: build candidate pool from deduped sources, then resolve each --type name.
            var allCandidates = orderedSources.SelectMany(s => s.Types).ToList();

            var pipeline = new List<Type>(typeNames.Count);
            foreach (var name in typeNames)
            {
                var isFullyQualified = name.Contains('.');
                var matches = isFullyQualified
                    ? allCandidates.Where(t => string.Equals(t.FullName, name, StringComparison.Ordinal)).ToList()
                    : allCandidates.Where(t => string.Equals(t.Name, name, StringComparison.Ordinal)).ToList();

                pipeline.Add(matches.Count switch
                {
                    0 => throw new InvalidOperationException($"Could not find rewriter type '{name}'."),
                    1 => matches[0],
                    _ => throw new InvalidOperationException(
                        $"Ambiguous rewriter type '{name}' matches multiple types: {string.Join(", ", matches.Select(t => t.FullName))} — use a fully-qualified name to disambiguate.")
                });
            }
            return pipeline;
        }
    }
}
