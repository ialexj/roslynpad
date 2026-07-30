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
        var pipelineTypes = new RewriterPipelineResolver(loader).Resolve(request.RewriterPaths, request.TypeNames);

        var originalRoots = new SyntaxNode[targetDocumentIds.Count];
        for (var index = 0; index < targetDocumentIds.Count; index++)
        {
            var document = solution.GetDocument(targetDocumentIds[index])
                ?? throw new InvalidOperationException("Target document not found in solution.");
            originalRoots[index] = await document.GetSyntaxRootAsync(cancellationToken)
                ?? throw new InvalidOperationException("The document syntax root could not be loaded.");
        }

        var currentSolution = solution;
        var currentRoots = (SyntaxNode[])originalRoots.Clone();
        var maxIterations = request.RepeatUntilStable ? request.MaxIterations : 1;
        var converged = false;
        var iterations = 0;

        for (var round = 1; round <= maxIterations; round++)
        {
            iterations = round;

            // Every document in this round reads from the same frozen snapshot, so a fix made to one
            // file only becomes visible to another file's semantic model on the *next* round.
            var roundSolution = currentSolution;
            var roundRoots = new SyntaxNode[targetDocumentIds.Count];

            await Parallel.ForEachAsync(
                Enumerable.Range(0, targetDocumentIds.Count),
                new ParallelOptions { CancellationToken = cancellationToken },
                async (index, token) =>
                {
                    roundRoots[index] = await RunPipelineAsync(pipelineTypes, roundSolution, targetDocumentIds[index], token);
                });

            var roundChanged = false;
            for (var index = 0; index < targetDocumentIds.Count; index++)
            {
                if (!roundRoots[index].IsEquivalentTo(currentRoots[index]))
                {
                    roundChanged = true;
                }

                // Fold this round's tree object (not text) back into the solution so annotations survive into the next round.
                currentSolution = currentSolution.WithDocumentSyntaxRoot(targetDocumentIds[index], roundRoots[index]);
            }

            currentRoots = roundRoots;

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

    private async Task<SyntaxNode> RunPipelineAsync(IReadOnlyList<Type> pipelineTypes, Solution solution, DocumentId documentId, CancellationToken cancellationToken)
    {
        var document = solution.GetDocument(documentId)
            ?? throw new InvalidOperationException("Target document not found in solution.");
        var currentRoot = await document.GetSyntaxRootAsync(cancellationToken)
            ?? throw new InvalidOperationException("The document syntax root could not be loaded.");

        // Track a document snapshot so we can recompute SemanticModel when a stage needs it.
        var currentDocument = document;
        SemanticModel? currentSemanticModel = null;

        foreach (var stageType in pipelineTypes)
        {
            if (RewriterActivator.RequiresSemanticModel(stageType))
            {
                // Update the document to the current in-memory tree before getting a fresh SemanticModel.
                currentDocument = currentDocument.WithSyntaxRoot(currentRoot);
                currentSemanticModel = await currentDocument.GetSemanticModelAsync(cancellationToken);
            }

            var rewriterInstance = (CSharpSyntaxRewriter)activator.Create(stageType, currentDocument, currentSemanticModel, cancellationToken);
            currentRoot = rewriterInstance.Visit(currentRoot)
                ?? throw new InvalidOperationException($"Rewriter '{stageType.FullName}' returned null from Visit.");
        }

        return currentRoot;
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
