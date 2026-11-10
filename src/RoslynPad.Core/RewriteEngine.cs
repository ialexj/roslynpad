using System.Collections.Concurrent;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace RoslynPad.Core;

public sealed class RewriteEngine
{
    private readonly RewriterLoader loader = new();
    private readonly RewriterActivator activator = new();

    public async Task<RewriteResult> RunAsync(RewriteRequest request, string targetFilePath, CancellationToken cancellationToken)
    {
        return await RunAsync(request, [targetFilePath], cancellationToken);
    }

    public async Task<RewriteResult> RunAsync(RewriteRequest request, IReadOnlyCollection<string> targetFilePaths, CancellationToken cancellationToken)
    {
        var (solution, documentIds) = await BuildAdhocSolutionAsync(targetFilePaths, cancellationToken);
        return await RunAsync(request, solution, documentIds, cancellationToken);
    }

    /// <summary>Runs the pipeline over documents that already belong to one shared Solution, so semantic models can see across files.</summary>
    public async Task<RewriteResult> RunAsync(RewriteRequest request, Solution solution, IReadOnlyList<DocumentId> targetDocumentIds, CancellationToken cancellationToken)
    {
        var pipelineTypes = new RewriterPipelineResolver(loader).Resolve(request.RewriterPaths, request.TypeNames, request.EmitDebugSymbols);

        var originalRoots = new SyntaxNode[targetDocumentIds.Count];
        for (var index = 0; index < targetDocumentIds.Count; index++)
        {
            var document = solution.GetDocument(targetDocumentIds[index])
                ?? throw new InvalidOperationException("Target document not found in solution.");
            originalRoots[index] = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new InvalidOperationException("The document syntax root could not be loaded.");
        }

        var state = new ConcurrentDictionary<object, object>();
        var currentSolution = solution;
        var currentRoots = (SyntaxNode[])originalRoots.Clone();
        var maxIterations = request.RepeatUntilStable ? request.MaxIterations : 1;
        var converged = false;
        var iterations = 0;

        for (var round = 1; round <= maxIterations; round++)
        {
            iterations = round;

            // Each rewriter stage processes ALL target files before the next stage begins.
            // Folding a stage's changes back into the Solution before the next stage starts
            // means a later stage's SemanticModel sees earlier stages' edits across every
            // file in the same pass — a fix in file A is visible to file B's semantic model
            // in the very next stage, without waiting for a repeat-until-stable round.
            var roundChanged = false;

            foreach (var stageType in pipelineTypes)
            {
                // Every document in this stage reads from the same frozen snapshot, so a fix
                // made to one file only becomes visible to another file's semantic model in
                // the *next* stage (or the next round).
                var stageSolution = currentSolution;
                var stageRoots = new SyntaxNode[targetDocumentIds.Count];

                await Parallel.ForEachAsync(
                    Enumerable.Range(0, targetDocumentIds.Count),
                    new ParallelOptions { CancellationToken = cancellationToken },
                    async (index, token) =>
                    {
                        stageRoots[index] = await RunStageAsync(stageType, stageSolution, targetDocumentIds[index], state, token);
                    });

                for (var index = 0; index < targetDocumentIds.Count; index++)
                {
                    if (!stageRoots[index].IsEquivalentTo(currentRoots[index]))
                    {
                        roundChanged = true;
                    }

                    // Fold this stage's tree object (not text) back into the solution so
                    // annotations survive into the next stage.
                    currentSolution = currentSolution.WithDocumentSyntaxRoot(targetDocumentIds[index], stageRoots[index]);
                }

                currentRoots = stageRoots;
            }

            if (!request.RepeatUntilStable || !roundChanged)
            {
                converged = true;
                break;
            }
        }

        var fileResults = new FileRewriteResult[targetDocumentIds.Count];
        for (var index = 0; index < targetDocumentIds.Count; index++)
        {
            var document = solution.GetDocument(targetDocumentIds[index])!;
            fileResults[index] = new FileRewriteResult(
                document.FilePath ?? document.Name,
                originalRoots[index].ToFullString(),
                currentRoots[index].ToFullString(),
                !currentRoots[index].IsEquivalentTo(originalRoots[index]));
        }

        return new RewriteResult(fileResults, converged, iterations);
    }

    private async Task<SyntaxNode> RunStageAsync(Type stageType, Solution solution, DocumentId documentId, ConcurrentDictionary<object, object> state, CancellationToken cancellationToken)
    {
        var document = solution.GetDocument(documentId)
            ?? throw new InvalidOperationException("Target document not found in solution.");
        var currentRoot = await document.GetSyntaxRootAsync(cancellationToken)
            ?? throw new InvalidOperationException("The document syntax root could not be loaded.");

        SemanticModel? currentSemanticModel = null;
        Compilation? currentCompilation = null;
        if (RewriterActivator.RequiresSemanticModel(stageType))
        {
            // The document already carries the current in-memory tree (folded in by the
            // previous stage), so a fresh SemanticModel reflects every prior stage's edits.
            // The Compilation is recomputed here too, so a stage that asks for a Compilation
            // sees the symbols produced by every earlier stage across all files.
            currentSemanticModel = await document.GetSemanticModelAsync(cancellationToken);
            currentCompilation = currentSemanticModel?.Compilation;
        }

        var rewriterInstance = (CSharpSyntaxRewriter)activator.Create(stageType, document, currentSemanticModel, currentCompilation, state, cancellationToken);
        return rewriterInstance.Visit(currentRoot)
            ?? throw new InvalidOperationException($"Rewriter '{stageType.FullName}' returned null from Visit.");
    }

    /// <summary>Hosts loose (non-project) target files as sibling documents in one Solution, with framework references so semantic models resolve BCL types.</summary>
    private static async Task<(Solution Solution, IReadOnlyList<DocumentId> DocumentIds)> BuildAdhocSolutionAsync(IReadOnlyCollection<string> targetFilePaths, CancellationToken cancellationToken)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "InMemory",
            assemblyName: "InMemoryAssembly",
            language: LanguageNames.CSharp,
            filePath: null,
            outputFilePath: null,
            metadataReferences: RewriterLoader.GetReferences());
        projectInfo = projectInfo.WithParseOptions(new CSharpParseOptions(LanguageVersion.Latest));
        var solution = workspace.CurrentSolution.AddProject(projectInfo);

        var documentIds = new List<DocumentId>(targetFilePaths.Count);
        foreach (var path in targetFilePaths)
        {
            var sourceText = SourceText.From(await File.ReadAllTextAsync(path, cancellationToken), Encoding.UTF8);
            var documentId = DocumentId.CreateNewId(projectId);
            solution = solution.AddDocument(documentId, Path.GetFileName(path), sourceText, filePath: path);
            documentIds.Add(documentId);
        }

        return (solution, documentIds);
    }
}
