namespace RoslynPad.Core;

public sealed class RewriteResult
{
    public RewriteResult(IReadOnlyList<FileRewriteResult> fileResults)
    {
        FileResults = fileResults;
    }

    public IReadOnlyList<FileRewriteResult> FileResults { get; }

    public bool HasAnyChanges => FileResults.Any(static fileResult => fileResult.HasChanges);
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
