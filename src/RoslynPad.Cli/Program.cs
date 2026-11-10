using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using RoslynPad.Core;

var rootCommand = new RootCommand("""
    RoslynPad applies user-provided C# syntax rewriters to source files, projects, or solutions.
    Rewriters are compiled from .cs files or loaded from .dll files. By default, all top-level
    rewriter classes in source files run in declaration order; repeated --type options instead
    define the ordered pipeline, and are required when loading a .dll.

    Use "diff" to compute a preview without changing files. It writes JSON to stdout containing
    changed file paths and their old and new text. Use "apply" to run the rewrite and write
    changed files to disk; it reruns the pipeline rather than consuming a previous diff. For a
    safe workflow, run diff, review the output, then apply with the same inputs and unchanged
    target files. Specify exactly one target with --file, --project, or --solution. Project and
    solution targets include C# documents and exclude bin and obj directories.

    The pipeline can be repeated with --repeat-until-stable until a round makes no changes or
    --max-iterations is reached. Each invocation is independent; Roslyn context and shared
    run-scoped state can be provided to rewriter constructors.
    """);

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
    Description = "Return detailed exit codes: 0 when changes are made, 1 when no changes occur, 2 for rewriter/load errors, and 3 when --repeat-until-stable does not converge."
};

var repeatUntilStableOption = new Option<bool>("--repeat-until-stable")
{
    Description = "Re-run the full pipeline against the previous round's in-memory result until a round makes no changes across all target files, or --max-iterations is reached."
};

var maxIterationsOption = new Option<int>("--max-iterations")
{
    Description = "Caps the number of --repeat-until-stable rounds. Only meaningful alongside --repeat-until-stable. Defaults to 10."
};
maxIterationsOption.SetDefaultValue(10);

var waitForDebuggerOption = new Option<bool>("--wait-for-debugger")
{
    Description = "Block until a debugger (e.g. Visual Studio) is attached to this process, then continue. Source-compiled rewriters are also emitted with a portable PDB so source-level breakpoints bind."
};

static async Task<ResolvedTarget> ResolveTargetAsync(string? filePath, string? projectPath, string? solutionPath, CancellationToken cancellationToken)
{
    var selectedTargets = new[] { filePath, projectPath, solutionPath }.Count(path => !string.IsNullOrWhiteSpace(path));
    if (selectedTargets != 1)
    {
        throw new ArgumentException("Specify exactly one of --file, --project, or --solution.");
    }

    if (!string.IsNullOrWhiteSpace(filePath))
    {
        return new ResolvedTarget(Path.GetFullPath(filePath), null, [], null);
    }

    MSBuildLocator.RegisterDefaults();

    // Kept open (not disposed) until the engine call completes so the real Solution/references it produced stay usable.
    var workspace = MSBuildWorkspace.Create();
    Solution solution;
    IEnumerable<Document> candidateDocuments;

    if (!string.IsNullOrWhiteSpace(projectPath))
    {
        var project = await workspace.OpenProjectAsync(projectPath, progress: null, cancellationToken);
        solution = project.Solution;
        candidateDocuments = project.Documents;
    }
    else
    {
        solution = await workspace.OpenSolutionAsync(solutionPath!, progress: null, cancellationToken);
        candidateDocuments = solution.Projects.SelectMany(project => project.Documents);
    }

    var documentIds = new List<DocumentId>();
    var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var document in candidateDocuments)
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

        if (seenPaths.Add(Path.GetFullPath(documentPath)))
        {
            documentIds.Add(document.Id);
        }
    }

    return new ResolvedTarget(null, solution, documentIds, workspace);
}

static async Task<RewriteResult> RunAsync(
    string[] rewriterPaths,
    string[] rewriterTypes,
    string? filePath,
    string? projectPath,
    string? solutionPath,
    bool repeatUntilStable,
    int maxIterations,
    bool emitDebugSymbols)
{
    var engine = new RewriteEngine();
    var request = new RewriteRequest(rewriterPaths, rewriterTypes, repeatUntilStable, maxIterations, emitDebugSymbols);
    var resolved = await ResolveTargetAsync(filePath, projectPath, solutionPath, CancellationToken.None);
    try
    {
        return resolved.LooseFilePath is { } loosePath
            ? await engine.RunAsync(request, loosePath, CancellationToken.None)
            : await engine.RunAsync(request, resolved.Solution!, resolved.DocumentIds, CancellationToken.None);
    }
    finally
    {
        resolved.Workspace?.Dispose();
    }
}

static async Task<int> RunWithExitCodeAsync(
    bool detailedExitCode,
    string[] rewriterPaths,
    string[] rewriterTypes,
    string? filePath,
    string? projectPath,
    string? solutionPath,
    bool repeatUntilStable,
    int maxIterations,
    bool emitDebugSymbols,
    Func<RewriteResult, Task> onSuccessAsync)
{
    try
    {
        var result = await RunAsync(rewriterPaths, rewriterTypes, filePath, projectPath, solutionPath, repeatUntilStable, maxIterations, emitDebugSymbols);
        await onSuccessAsync(result);

        if (repeatUntilStable && !result.Converged)
        {
            return detailedExitCode ? 3 : 1;
        }

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
diffCommand.AddOption(repeatUntilStableOption);
diffCommand.AddOption(maxIterationsOption);
diffCommand.AddOption(waitForDebuggerOption);

diffCommand.SetHandler(async (InvocationContext context) =>
{
    var detailedExitCode = context.ParseResult.GetValueForOption(detailedExitCodeOption);
    var rewriterPaths = context.ParseResult.GetValueForOption(rewriterOption) ?? [];
    var rewriterTypes = context.ParseResult.GetValueForOption(typeOption) ?? [];
    var targetFile = context.ParseResult.GetValueForOption(fileOption);
    var targetProject = context.ParseResult.GetValueForOption(projectOption);
    var targetSolution = context.ParseResult.GetValueForOption(solutionOption);
    var repeatUntilStable = context.ParseResult.GetValueForOption(repeatUntilStableOption);
    var maxIterations = context.ParseResult.GetValueForOption(maxIterationsOption);
    var waitForDebugger = context.ParseResult.GetValueForOption(waitForDebuggerOption);

    if (waitForDebugger)
    {
        WaitForDebugger();
    }

    var exitCode = await RunWithExitCodeAsync(detailedExitCode, rewriterPaths, rewriterTypes, targetFile, targetProject, targetSolution, repeatUntilStable, maxIterations, waitForDebugger, async result =>
    {
        var jsonResult = new RewriteResult(
            result.FileResults.Where(static fileResult => fileResult.HasChanges).ToList(),
            result.Converged,
            result.Iterations);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(jsonResult, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        await Task.CompletedTask;
    });

    context.ExitCode = exitCode;
});

var applyCommand = new Command("apply", "Run the rewrite pipeline and write changed files to the selected target");
applyCommand.AddOption(rewriterOption);
applyCommand.AddOption(typeOption);
applyCommand.AddOption(fileOption);
applyCommand.AddOption(projectOption);
applyCommand.AddOption(solutionOption);
applyCommand.AddOption(detailedExitCodeOption);
applyCommand.AddOption(repeatUntilStableOption);
applyCommand.AddOption(maxIterationsOption);
applyCommand.AddOption(waitForDebuggerOption);

applyCommand.SetHandler(async (InvocationContext context) =>
{
    var detailedExitCode = context.ParseResult.GetValueForOption(detailedExitCodeOption);
    var rewriterPaths = context.ParseResult.GetValueForOption(rewriterOption) ?? [];
    var rewriterTypes = context.ParseResult.GetValueForOption(typeOption) ?? [];
    var targetFile = context.ParseResult.GetValueForOption(fileOption);
    var targetProject = context.ParseResult.GetValueForOption(projectOption);
    var targetSolution = context.ParseResult.GetValueForOption(solutionOption);
    var repeatUntilStable = context.ParseResult.GetValueForOption(repeatUntilStableOption);
    var maxIterations = context.ParseResult.GetValueForOption(maxIterationsOption);
    var waitForDebugger = context.ParseResult.GetValueForOption(waitForDebuggerOption);

    if (waitForDebugger)
    {
        WaitForDebugger();
    }

    var exitCode = await RunWithExitCodeAsync(detailedExitCode, rewriterPaths, rewriterTypes, targetFile, targetProject, targetSolution, repeatUntilStable, maxIterations, waitForDebugger, async result =>
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

static void WaitForDebugger()
{
    if (Debugger.IsAttached)
    {
        return;
    }

    Console.Error.WriteLine($"Process {Environment.ProcessId} ({Environment.ProcessPath}) waiting for a debugger to attach...");
    while (!Debugger.IsAttached)
    {
        Thread.Sleep(100);
    }
    Console.Error.WriteLine("Debugger attached; continuing.");
}

sealed record ResolvedTarget(string? LooseFilePath, Solution? Solution, IReadOnlyList<DocumentId> DocumentIds, MSBuildWorkspace? Workspace);
