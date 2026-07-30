using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RoslynPad.Core;

namespace RoslynPad.Core.Tests;

public sealed class RewriteEngineTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static async Task<string> WriteTempFileAsync(string dir, string name, string content)
    {
        var path = Path.Combine(dir, name);
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "roslynpad-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<string> CompileToDllAsync(string dir, string name, string sourceCode)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("TRUSTED_PLATFORM_ASSEMBLIES unavailable.");
        var references = trustedPlatformAssemblies
            .Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => MetadataReference.CreateFromFile(p))
            .Cast<MetadataReference>()
            .Append(MetadataReference.CreateFromFile(typeof(CSharpSyntaxRewriter).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(SemanticModel).Assembly.Location))
            .ToList();

        var compilation = CSharpCompilation.Create(
            name,
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var dllPath = Path.Combine(dir, name + ".dll");
        using var fs = File.Create(dllPath);
        var result = compilation.Emit(fs);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return dllPath;
    }

    // -----------------------------------------------------------------------
    // Single rewriter (baseline)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Rewrites_a_file_and_injects_semantic_model()
    {
        var dir = MakeTempDir();
        try
        {
            var targetPath = await WriteTempFileAsync(dir, "Sample.cs", "class Sample { public void M() { } }\n");
            var rewriterPath = await WriteTempFileAsync(dir, "Rewriter.cs", """
                using Microsoft.CodeAnalysis;
                using Microsoft.CodeAnalysis.CSharp;
                using Microsoft.CodeAnalysis.CSharp.Syntax;

                public sealed class RenameMethodRewriter : CSharpSyntaxRewriter
                {
                    private readonly SemanticModel semanticModel;

                    public RenameMethodRewriter(SemanticModel semanticModel)
                    {
                        this.semanticModel = semanticModel;
                    }

                    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
                    {
                        return node.Identifier.ValueText == "M"
                            ? node.WithIdentifier(SyntaxFactory.Identifier("Renamed"))
                            : base.VisitMethodDeclaration(node);
                    }
                }
                """);

            var result = await new RewriteEngine().RunAsync(
                new RewriteRequest([rewriterPath], ["RenameMethodRewriter"]),
                targetPath, CancellationToken.None);

            var fileResult = Assert.Single(result.FileResults);
            Assert.True(fileResult.HasChanges);
            Assert.Contains("Renamed", fileResult.NewText);
            Assert.DoesNotContain("public void M()", fileResult.NewText);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Batch_overload_rewrites_multiple_files()
    {
        var dir = MakeTempDir();
        try
        {
            var fileA = await WriteTempFileAsync(dir, "A.cs", "class A { public void M() { } }\n");
            var fileB = await WriteTempFileAsync(dir, "B.cs", "class B { public void M() { } }\n");
            var rewriterPath = await WriteTempFileAsync(dir, "Rewriter.cs", """
                using Microsoft.CodeAnalysis;
                using Microsoft.CodeAnalysis.CSharp;
                using Microsoft.CodeAnalysis.CSharp.Syntax;

                public sealed class RenameMethodRewriter : CSharpSyntaxRewriter
                {
                    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
                        node.WithIdentifier(SyntaxFactory.Identifier("Renamed"));
                }
                """);

            var result = await new RewriteEngine().RunAsync(
                new RewriteRequest([rewriterPath], ["RenameMethodRewriter"]),
                [fileA, fileB],
                CancellationToken.None);

            Assert.Equal(2, result.FileResults.Count);
            Assert.All(result.FileResults, fileResult => Assert.True(fileResult.HasChanges));
            Assert.Contains(result.FileResults, fileResult => fileResult.FilePath == fileA && fileResult.NewText.Contains("Renamed"));
            Assert.Contains(result.FileResults, fileResult => fileResult.FilePath == fileB && fileResult.NewText.Contains("Renamed"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // -----------------------------------------------------------------------
    // Multi-rewriter: declaration-order default (no --type)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Two_rewriter_classes_in_one_file_run_in_declaration_order_by_default()
    {
        // First rewriter renames M → Intermediate; second renames Intermediate → Final.
        // If they ran in reverse order, neither rename would match.
        var dir = MakeTempDir();
        try
        {
            var targetPath = await WriteTempFileAsync(dir, "Sample.cs", "class Sample { public void M() { } }\n");
            var rewriterPath = await WriteTempFileAsync(dir, "TwoRewriters.cs", """
                using Microsoft.CodeAnalysis;
                using Microsoft.CodeAnalysis.CSharp;
                using Microsoft.CodeAnalysis.CSharp.Syntax;

                public sealed class FirstRewriter : CSharpSyntaxRewriter
                {
                    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
                        node.Identifier.ValueText == "M"
                            ? node.WithIdentifier(SyntaxFactory.Identifier("Intermediate"))
                            : base.VisitMethodDeclaration(node);
                }

                public sealed class SecondRewriter : CSharpSyntaxRewriter
                {
                    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
                        node.Identifier.ValueText == "Intermediate"
                            ? node.WithIdentifier(SyntaxFactory.Identifier("Final"))
                            : base.VisitMethodDeclaration(node);
                }
                """);

            var result = await new RewriteEngine().RunAsync(
                new RewriteRequest([rewriterPath]),
                targetPath, CancellationToken.None);

            var fileResult = Assert.Single(result.FileResults);
            Assert.True(fileResult.HasChanges);
            Assert.Contains("Final", fileResult.NewText);
            Assert.DoesNotContain("Intermediate", fileResult.NewText);
            Assert.DoesNotContain("void M()", fileResult.NewText);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // -----------------------------------------------------------------------
    // Multi-rewriter: explicit --type overrides order across two files
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Explicit_type_list_overrides_rewriter_file_order()
    {
        // FileA declares AddSuffix (appends _A); FileB declares AddPrefix (prepends B_).
        // With --type AddPrefix AddSuffix the prefix stage should run first: B_M, then B_M_A.
        var dir = MakeTempDir();
        try
        {
            var targetPath = await WriteTempFileAsync(dir, "Sample.cs", "class Sample { public void M() { } }\n");
            var fileA = await WriteTempFileAsync(dir, "A.cs", """
                using Microsoft.CodeAnalysis;
                using Microsoft.CodeAnalysis.CSharp;
                using Microsoft.CodeAnalysis.CSharp.Syntax;

                public sealed class AddSuffix : CSharpSyntaxRewriter
                {
                    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
                        node.WithIdentifier(SyntaxFactory.Identifier(node.Identifier.ValueText + "_A"));
                }
                """);
            var fileB = await WriteTempFileAsync(dir, "B.cs", """
                using Microsoft.CodeAnalysis;
                using Microsoft.CodeAnalysis.CSharp;
                using Microsoft.CodeAnalysis.CSharp.Syntax;

                public sealed class AddPrefix : CSharpSyntaxRewriter
                {
                    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
                        node.WithIdentifier(SyntaxFactory.Identifier("B_" + node.Identifier.ValueText));
                }
                """);

            // --rewriter A.cs B.cs --type AddPrefix AddSuffix  (prefix before suffix)
            var result = await new RewriteEngine().RunAsync(
                new RewriteRequest([fileA, fileB], ["AddPrefix", "AddSuffix"]),
                targetPath, CancellationToken.None);

            var fileResult = Assert.Single(result.FileResults);
            Assert.True(fileResult.HasChanges);
            // AddPrefix ran first: M → B_M; then AddSuffix: B_M → B_M_A
            Assert.Contains("B_M_A", fileResult.NewText);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // -----------------------------------------------------------------------
    // Ambiguous --type (same simple name in two source files)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Ambiguous_type_name_throws_with_disambiguation_message()
    {
        var dir = MakeTempDir();
        try
        {
            var targetPath = await WriteTempFileAsync(dir, "Sample.cs", "class Sample { }\n");
            var fileA = await WriteTempFileAsync(dir, "A.cs", """
                using Microsoft.CodeAnalysis.CSharp;
                public sealed class MyRewriter : CSharpSyntaxRewriter { }
                """);
            var fileB = await WriteTempFileAsync(dir, "B.cs", """
                using Microsoft.CodeAnalysis.CSharp;
                public sealed class MyRewriter : CSharpSyntaxRewriter { }
                """);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new RewriteEngine().RunAsync(
                    new RewriteRequest([fileA, fileB], ["MyRewriter"]),
                    targetPath, CancellationToken.None));

            Assert.Contains("Ambiguous", ex.Message);
            Assert.Contains("MyRewriter", ex.Message);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // -----------------------------------------------------------------------
    // Precompiled .dll rewriter
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Dll_rewriter_with_explicit_type_succeeds()
    {
        var dir = MakeTempDir();
        try
        {
            var targetPath = await WriteTempFileAsync(dir, "Sample.cs", "class Sample { public void M() { } }\n");
            const string rewriterSource = """
                using Microsoft.CodeAnalysis;
                using Microsoft.CodeAnalysis.CSharp;
                using Microsoft.CodeAnalysis.CSharp.Syntax;

                public sealed class DllRewriter : CSharpSyntaxRewriter
                {
                    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
                        node.WithIdentifier(SyntaxFactory.Identifier("FromDll"));
                }
                """;
            var dllPath = await CompileToDllAsync(dir, "DllRewriter", rewriterSource);

            var result = await new RewriteEngine().RunAsync(
                new RewriteRequest([dllPath], ["DllRewriter"]),
                targetPath, CancellationToken.None);

            var fileResult = Assert.Single(result.FileResults);
            Assert.True(fileResult.HasChanges);
            Assert.Contains("FromDll", fileResult.NewText);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Dll_rewriter_without_type_throws_mandatory_type_error()
    {
        var dir = MakeTempDir();
        try
        {
            var targetPath = await WriteTempFileAsync(dir, "Sample.cs", "class Sample { }\n");
            const string rewriterSource = """
                using Microsoft.CodeAnalysis.CSharp;
                public sealed class DllRewriter : CSharpSyntaxRewriter { }
                """;
            var dllPath = await CompileToDllAsync(dir, "DllRewriter2", rewriterSource);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new RewriteEngine().RunAsync(
                    new RewriteRequest([dllPath]),
                    targetPath, CancellationToken.None));

            Assert.Contains("--type is required", ex.Message);
            Assert.Contains(".dll", ex.Message);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // -----------------------------------------------------------------------
    // --repeat-until-stable / --max-iterations
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Repeat_until_stable_converges_after_multiple_rounds()
    {
        var dir = MakeTempDir();
        try
        {
            var targetPath = await WriteTempFileAsync(dir, "Sample.cs", "class Sample { public void M() { } }\n");
            var rewriterPath = await WriteTempFileAsync(dir, "IncrementRewriter.cs", """
                using Microsoft.CodeAnalysis;
                using Microsoft.CodeAnalysis.CSharp;
                using Microsoft.CodeAnalysis.CSharp.Syntax;

                public sealed class IncrementRewriter : CSharpSyntaxRewriter
                {
                    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
                    {
                        var name = node.Identifier.ValueText;
                        var underscoreIndex = name.IndexOf('_');
                        var count = underscoreIndex < 0 ? 0 : int.Parse(name[(underscoreIndex + 1)..]);
                        if (count >= 3)
                            return base.VisitMethodDeclaration(node);

                        var baseName = underscoreIndex < 0 ? name : name[..underscoreIndex];
                        return node.WithIdentifier(SyntaxFactory.Identifier($"{baseName}_{count + 1}"));
                    }
                }
                """);

            var result = await new RewriteEngine().RunAsync(
                new RewriteRequest([rewriterPath], ["IncrementRewriter"], repeatUntilStable: true, maxIterations: 10),
                targetPath, CancellationToken.None);

            var fileResult = Assert.Single(result.FileResults);
            Assert.True(result.Converged);
            Assert.Equal(4, result.Iterations); // 3 rounds that change the name, plus 1 confirming round with no changes
            Assert.Contains("M_3", fileResult.NewText);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Repeat_until_stable_reports_non_convergence_when_max_iterations_is_reached()
    {
        var dir = MakeTempDir();
        try
        {
            var targetPath = await WriteTempFileAsync(dir, "Sample.cs", "class Sample { public void M() { } }\n");
            var rewriterPath = await WriteTempFileAsync(dir, "ToggleRewriter.cs", """
                using Microsoft.CodeAnalysis;
                using Microsoft.CodeAnalysis.CSharp;
                using Microsoft.CodeAnalysis.CSharp.Syntax;

                public sealed class ToggleRewriter : CSharpSyntaxRewriter
                {
                    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
                        node.WithIdentifier(SyntaxFactory.Identifier(node.Identifier.ValueText == "A" ? "B" : "A"));
                }
                """);

            var result = await new RewriteEngine().RunAsync(
                new RewriteRequest([rewriterPath], ["ToggleRewriter"], repeatUntilStable: true, maxIterations: 4),
                targetPath, CancellationToken.None);

            var fileResult = Assert.Single(result.FileResults);
            Assert.False(result.Converged);
            Assert.Equal(4, result.Iterations);
            Assert.True(fileResult.HasChanges);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Repeat_until_stable_preserves_annotations_added_in_an_earlier_round()
    {
        // Round 1 marks the method with a SyntaxAnnotation and renames it once; round 2 only takes its
        // second rename branch if it can still see that annotation on the incoming node - which requires
        // the tree object (not reparsed text) to have carried it forward from round 1.
        var dir = MakeTempDir();
        try
        {
            var targetPath = await WriteTempFileAsync(dir, "Sample.cs", "class Sample { public void M() { } }\n");
            var rewriterPath = await WriteTempFileAsync(dir, "MarkOnceRewriter.cs", """
                using Microsoft.CodeAnalysis;
                using Microsoft.CodeAnalysis.CSharp;
                using Microsoft.CodeAnalysis.CSharp.Syntax;

                public sealed class MarkOnceRewriter : CSharpSyntaxRewriter
                {
                    private static readonly SyntaxAnnotation Visited = new("Visited");

                    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
                    {
                        if (node.HasAnnotation(Visited))
                        {
                            return node.Identifier.ValueText == "Marked"
                                ? node.WithIdentifier(SyntaxFactory.Identifier("ConfirmedAnnotated"))
                                : base.VisitMethodDeclaration(node);
                        }

                        return node.Identifier.ValueText == "M"
                            ? node.WithIdentifier(SyntaxFactory.Identifier("Marked")).WithAdditionalAnnotations(Visited)
                            : base.VisitMethodDeclaration(node);
                    }
                }
                """);

            var result = await new RewriteEngine().RunAsync(
                new RewriteRequest([rewriterPath], ["MarkOnceRewriter"], repeatUntilStable: true, maxIterations: 5),
                targetPath, CancellationToken.None);

            var fileResult = Assert.Single(result.FileResults);
            Assert.True(result.Converged);
            Assert.Equal(3, result.Iterations);
            Assert.Contains("ConfirmedAnnotated", fileResult.NewText);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Repeat_until_stable_lets_a_semantic_fix_in_one_file_unlock_a_rewrite_in_another_file()
    {
        // File A's syntactic rename (Helper.M -> Ready) only becomes visible to File B's semantic-model
        // check on the *next* round, since one round's files all read from the same frozen snapshot.
        var dir = MakeTempDir();
        try
        {
            var fileA = await WriteTempFileAsync(dir, "A.cs", "public class Helper { public void M() { } }\n");
            var fileB = await WriteTempFileAsync(dir, "B.cs", "public class Consumer { public void M() { } }\n");
            var rewriterPath = await WriteTempFileAsync(dir, "CrossFileRewriter.cs", """
                using System.Linq;
                using Microsoft.CodeAnalysis;
                using Microsoft.CodeAnalysis.CSharp;
                using Microsoft.CodeAnalysis.CSharp.Syntax;

                public sealed class CrossFileRewriter : CSharpSyntaxRewriter
                {
                    private readonly SemanticModel semanticModel;

                    public CrossFileRewriter(SemanticModel semanticModel)
                    {
                        this.semanticModel = semanticModel;
                    }

                    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
                    {
                        var containingClass = node.Parent as ClassDeclarationSyntax;

                        if (containingClass?.Identifier.ValueText == "Helper" && node.Identifier.ValueText == "M")
                        {
                            return node.WithIdentifier(SyntaxFactory.Identifier("Ready"));
                        }

                        if (containingClass?.Identifier.ValueText == "Consumer" && node.Identifier.ValueText == "M")
                        {
                            var helperType = semanticModel.Compilation.GetTypeByMetadataName("Helper");
                            var isReady = helperType?.GetMembers("Ready").Any() == true;
                            return isReady
                                ? node.WithIdentifier(SyntaxFactory.Identifier("Confirmed"))
                                : base.VisitMethodDeclaration(node);
                        }

                        return base.VisitMethodDeclaration(node);
                    }
                }
                """);

            var result = await new RewriteEngine().RunAsync(
                new RewriteRequest([rewriterPath], ["CrossFileRewriter"], repeatUntilStable: true, maxIterations: 10),
                [fileA, fileB],
                CancellationToken.None);

            Assert.True(result.Converged);
            Assert.Equal(3, result.Iterations);
            Assert.Contains(result.FileResults, f => f.FilePath == fileA && f.NewText.Contains("Ready"));
            Assert.Contains(result.FileResults, f => f.FilePath == fileB && f.NewText.Contains("Confirmed"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
