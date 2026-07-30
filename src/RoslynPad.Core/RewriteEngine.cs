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
        var pipelineTypes = new RewriterPipelineResolver(loader).Resolve(request.RewriterPaths, request.TypeNames);

        var filePaths = targetFilePaths.ToArray();
        var fileResults = new FileRewriteResult[filePaths.Length];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, filePaths.Length),
            new ParallelOptions { CancellationToken = cancellationToken },
            async (index, token) =>
            {
                fileResults[index] = await RewriteFileAsync(pipelineTypes, filePaths[index], token);
            });

        return new RewriteResult(fileResults);
    }

    private async Task<FileRewriteResult> RewriteFileAsync(IReadOnlyList<Type> pipelineTypes, string targetFilePath, CancellationToken cancellationToken)
    {
        var sourceText = SourceText.From(await File.ReadAllTextAsync(targetFilePath, cancellationToken), Encoding.UTF8);
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var projectInfo = ProjectInfo.Create(projectId, VersionStamp.Create(), "InMemory", assemblyName: "InMemoryAssembly", language: LanguageNames.CSharp, filePath: null, outputFilePath: null);
        projectInfo = projectInfo.WithParseOptions(new CSharpParseOptions(LanguageVersion.Latest));
        _ = workspace.AddProject(projectInfo);
        var document = workspace.AddDocument(projectId, Path.GetFileName(targetFilePath), sourceText);

        var originalRoot = await document.GetSyntaxRootAsync(cancellationToken)
            ?? throw new InvalidOperationException("The document syntax root could not be loaded.");
        var currentRoot = originalRoot;

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

        return new FileRewriteResult(
            targetFilePath,
            originalRoot.ToFullString(),
            currentRoot.ToFullString(),
            !currentRoot.IsEquivalentTo(originalRoot));
    }
}
