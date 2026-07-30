namespace RoslynPad.Core;

public sealed class RewriteResult
{
    public RewriteResult(IReadOnlyList<FileRewriteResult> fileResults, bool converged = true, int iterations = 1)
    {
        FileResults = fileResults;
        Converged = converged;
        Iterations = iterations;
    }

    public IReadOnlyList<FileRewriteResult> FileResults { get; }

    public bool HasAnyChanges => FileResults.Any(static fileResult => fileResult.HasChanges);

    /// <summary>False when repeat-until-stable hit max-iterations without a round producing zero changes.</summary>
    public bool Converged { get; }

    /// <summary>Number of repeat-until-stable rounds actually run (always 1 when not repeating).</summary>
    public int Iterations { get; }
}

public sealed class FileRewriteResult
{
    public FileRewriteResult(string filePath, string oldText, string newText, bool hasChanges)
    {
        FilePath = filePath;
        OldText = oldText;
        NewText = newText;
        HasChanges = hasChanges;
    }

    public string FilePath { get; }

    public string OldText { get; }

    public string NewText { get; }

    public bool HasChanges { get; }
}
