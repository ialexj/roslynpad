using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using RoslynPad.Core;

var rootCommand = new RootCommand("RoslynPad CLI");

var rewriterOption = new Option<string[]>("--rewriter")
{
    Description = "Path to a .cs source file or precompiled .dll containing rewriter class(es). Repeatable; order defines the default pipeline.",
    IsRequired = true,
    AllowMultipleArgumentsPerToken = false,
};

var typeOption = new Option<string[]>("--type")
{
    Description = "Fully-qualified or simple name of a rewriter type. Repeatable; when any value is provided, the ordered list of --type values fully defines the pipeline. Required when any --rewriter is a .dll.",
    AllowMultipleArgumentsPerToken = false,
};

var fileOption = new Option<string>("--file")
{
    Description = "Path to a single file to rewrite."
};

var projectOption = new Option<string>("--project")
{
    Description = "Path to a .csproj to rewrite all C# documents in the project."
};

var solutionOption = new Option<string>("--solution")
{
    Description = "Path to a .sln to rewrite all C# documents in the solution."
};

var detailedExitCodeOption = new Option<bool>("--detailed-exit-code")
{
    Description = "Return detailed exit codes: 0 when changes are made, 1 when no changes occur, and 2 for rewriter/load errors."
};

static async Task<IReadOnlyCollection<string>> ResolveTargetFilesAsync(string? filePath, string? projectPath, string? solutionPath, CancellationToken cancellationToken)
{
    var selectedTargets = new[] { filePath, projectPath, solutionPath }.Count(path => !string.IsNullOrWhiteSpace(path));
    if (selectedTargets != 1)
    {
        throw new ArgumentException("Specify exactly one of --file, --project, or --solution.");
    }

    if (!string.IsNullOrWhiteSpace(filePath))
    {
        return [Path.GetFullPath(filePath)];
    }

    MSBuildLocator.RegisterDefaults();

    using var workspace = MSBuildWorkspace.Create();
    var targetDocuments = new List<Document>();

    if (!string.IsNullOrWhiteSpace(projectPath))
    {
        var project = await workspace.OpenProjectAsync(projectPath, progress: null, cancellationToken);
        targetDocuments.AddRange(project.Documents);
    }
    else if (!string.IsNullOrWhiteSpace(solutionPath))
    {
        var solution = await workspace.OpenSolutionAsync(solutionPath, progress: null, cancellationToken);
        foreach (var project in solution.Projects)
        {
            targetDocuments.AddRange(project.Documents);
        }
    }

    var uniquePaths = new List<string>();
    var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var document in targetDocuments)
    {
        if (document.FilePath is not { } documentPath)
            continue;

        if (!string.Equals(Path.GetExtension(documentPath), ".cs", StringComparison.OrdinalIgnoreCase))
            continue;

        var normalizedPath = documentPath.Replace('/', Path.DirectorySeparatorChar);
        if (normalizedPath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var fullPath = Path.GetFullPath(documentPath);
        if (seenPaths.Add(fullPath))
        {
            uniquePaths.Add(fullPath);
        }
    }

    return uniquePaths;
}

static async Task<RewriteResult> RunAsync(string[] rewriterPaths, string[] rewriterTypes, string? filePath, string? projectPath, string? solutionPath)
{
    var engine = new RewriteEngine();
    var request = new RewriteRequest(rewriterPaths, rewriterTypes);
    var targetFiles = await ResolveTargetFilesAsync(filePath, projectPath, solutionPath, CancellationToken.None);
    return await engine.RunAsync(request, targetFiles, CancellationToken.None);
}

static async Task<int> RunWithExitCodeAsync(
    bool detailedExitCode,
    string[] rewriterPaths,
    string[] rewriterTypes,
    string? filePath,
    string? projectPath,
    string? solutionPath,
    Func<RewriteResult, Task> onSuccessAsync)
{
    try
    {
        var result = await RunAsync(rewriterPaths, rewriterTypes, filePath, projectPath, solutionPath);
        await onSuccessAsync(result);
        return detailedExitCode ? (result.HasAnyChanges ? 0 : 1) : 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return detailedExitCode ? 2 : 1;
    }
}

var diffCommand = new Command("diff", "Compute a rewrite preview without modifying files");
diffCommand.AddOption(rewriterOption);
diffCommand.AddOption(typeOption);
diffCommand.AddOption(fileOption);
diffCommand.AddOption(projectOption);
diffCommand.AddOption(solutionOption);
diffCommand.AddOption(detailedExitCodeOption);

diffCommand.SetHandler(async (InvocationContext context) =>
{
    var detailedExitCode = context.ParseResult.GetValueForOption(detailedExitCodeOption);
    var rewriterPaths = context.ParseResult.GetValueForOption(rewriterOption) ?? [];
    var rewriterTypes = context.ParseResult.GetValueForOption(typeOption) ?? [];
    var targetFile = context.ParseResult.GetValueForOption(fileOption);
    var targetProject = context.ParseResult.GetValueForOption(projectOption);
    var targetSolution = context.ParseResult.GetValueForOption(solutionOption);

    var exitCode = await RunWithExitCodeAsync(detailedExitCode, rewriterPaths, rewriterTypes, targetFile, targetProject, targetSolution, async result =>
    {
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        await Task.CompletedTask;
    });

    context.ExitCode = exitCode;
});

var applyCommand = new Command("apply", "Apply the rewrite result to the target file");
applyCommand.AddOption(rewriterOption);
applyCommand.AddOption(typeOption);
applyCommand.AddOption(fileOption);
applyCommand.AddOption(projectOption);
applyCommand.AddOption(solutionOption);
applyCommand.AddOption(detailedExitCodeOption);

applyCommand.SetHandler(async (InvocationContext context) =>
{
    var detailedExitCode = context.ParseResult.GetValueForOption(detailedExitCodeOption);
    var rewriterPaths = context.ParseResult.GetValueForOption(rewriterOption) ?? [];
    var rewriterTypes = context.ParseResult.GetValueForOption(typeOption) ?? [];
    var targetFile = context.ParseResult.GetValueForOption(fileOption);
    var targetProject = context.ParseResult.GetValueForOption(projectOption);
    var targetSolution = context.ParseResult.GetValueForOption(solutionOption);

    var exitCode = await RunWithExitCodeAsync(detailedExitCode, rewriterPaths, rewriterTypes, targetFile, targetProject, targetSolution, async result =>
    {
        foreach (var fileResult in result.FileResults.Where(fileResult => fileResult.HasChanges))
        {
            File.WriteAllText(fileResult.FilePath, fileResult.NewText, System.Text.Encoding.UTF8);
        }

        await Task.CompletedTask;
    });

    context.ExitCode = exitCode;
});

rootCommand.Add(diffCommand);
rootCommand.Add(applyCommand);

return await rootCommand.InvokeAsync(args);
