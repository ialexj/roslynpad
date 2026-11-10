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
    public async Task Source_rewriter_uses_implicit_imports()
    {
        var dir = MakeTempDir();
        try
        {
            var targetPath = await WriteTempFileAsync(dir, "Sample.cs", "class Sample { public void M() { } }\n");
            var rewriterPath = await WriteTempFileAsync(dir, "Rewriter.cs", """
                public sealed class RenameMethodRewriter : CSharpSyntaxRewriter
                {
                    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
                    {
                        Debug.Assert(Enumerable.Empty<string>().Count() == 0);
                        var names = new List<string> { node.Identifier.ValueText };
                        return node.WithIdentifier(SyntaxFactory.Identifier(names.Single() + nameof(Environment)));
                    }
                }
                """);

            var result = await new RewriteEngine().RunAsync(
                new RewriteRequest([rewriterPath], ["RenameMethodRewriter"]),
                targetPath, CancellationToken.None);

            var fileResult = Assert.Single(result.FileResults);
            Assert.True(fileResult.HasChanges);
            Assert.Contains("MEnvironment", fileResult.NewText);
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

    // -----------------------------------------------------------------------
    // Multi-rewriter pipeline: per-stage (not per-file) processing
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Multi_rewriter_pipeline_processes_all_files_per_stage_before_next_stage()
    {
        // Stage 1 (RenameHelper) renames Helper.M -> Ready in file A.
        // Stage 2 (ConfirmWhenReady) renames Consumer.M -> Confirmed in file B, but ONLY if it
        // can see Helper.Ready via the semantic model. Under per-stage processing, stage 1's
        // edit to file A is folded into the Solution before stage 2 runs, so stage 2's semantic
        // model sees Helper.Ready in a single pass — no --repeat-until-stable needed.
        // (Under the old per-file pipeline, file B would run both stages against the original
        // snapshot and never see Helper.Ready without --repeat-until-stable.)
        var dir = MakeTempDir();
        try
        {
            var fileA = await WriteTempFileAsync(dir, "A.cs", "public class Helper { public void M() { } }\n");
            var fileB = await WriteTempFileAsync(dir, "B.cs", "public class Consumer { public void M() { } }\n");
            var rewriterPath = await WriteTempFileAsync(dir, "PipelineRewriters.cs", """
                using System.Linq;
                using Microsoft.CodeAnalysis;
                using Microsoft.CodeAnalysis.CSharp;
                using Microsoft.CodeAnalysis.CSharp.Syntax;

                public sealed class RenameHelper : CSharpSyntaxRewriter
                {
                    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
                    {
                        var containingClass = node.Parent as ClassDeclarationSyntax;
                        return containingClass?.Identifier.ValueText == "Helper" && node.Identifier.ValueText == "M"
                            ? node.WithIdentifier(SyntaxFactory.Identifier("Ready"))
                            : base.VisitMethodDeclaration(node);
                    }
                }

                public sealed class ConfirmWhenReady : CSharpSyntaxRewriter
                {
                    private readonly SemanticModel semanticModel;

                    public ConfirmWhenReady(SemanticModel semanticModel)
                    {
                        this.semanticModel = semanticModel;
                    }

                    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
                    {
                        var containingClass = node.Parent as ClassDeclarationSyntax;
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
                new RewriteRequest([rewriterPath], ["RenameHelper", "ConfirmWhenReady"]),
                [fileA, fileB],
                CancellationToken.None);

            Assert.True(result.Converged);
            Assert.Equal(1, result.Iterations); // single pass — no repeat-until-stable
            Assert.Contains(result.FileResults, f => f.FilePath == fileA && f.NewText.Contains("Ready"));
            Assert.Contains(result.FileResults, f => f.FilePath == fileB && f.NewText.Contains("Confirmed"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Three_stage_pipeline_collect_find_acts_with_fresh_compilation_per_stage()
    {
        // The canonical use case: stage 1 collects info across the whole solution, stage 2
        // finds references using a fresh compilation that reflects stage 1's edits, and stage 3
        // acts on those references — all in a single pass, no --repeat-until-stable.
        //
        // Stage 1 (AddMarkerField) adds a `public bool IsMarked;` field to every class whose
        // name ends with "Target". It takes a Compilation so it can enumerate all types.
        // Stage 2 (CountMarkedFields) takes a Compilation and counts how many types now have
        // an IsMarked field, recording the count in the shared state dictionary.
        // Stage 3 (AnnotateCount) reads the count from state and writes it as a comment on
        // the first class in each file.
        //
        // If the compilation were NOT refreshed between stages, stage 2 would see zero marked
        // fields (stage 1's additions wouldn't be visible) and the test would fail.
        var dir = MakeTempDir();
        try
        {
            var fileA = await WriteTempFileAsync(dir, "A.cs", "public class TargetA { public void M() { } }\n");
            var fileB = await WriteTempFileAsync(dir, "B.cs", "public class TargetB { public void M() { } }\npublic class Other { public void M() { } }\n");
            var rewriterPath = await WriteTempFileAsync(dir, "PipelineRewriters.cs", """
                using System.Linq;
                using Microsoft.CodeAnalysis;
                using Microsoft.CodeAnalysis.CSharp;
                using Microsoft.CodeAnalysis.CSharp.Syntax;

                public sealed class AddMarkerField : CSharpSyntaxRewriter
                {
                    private readonly Compilation compilation;

                    public AddMarkerField(Compilation compilation) => this.compilation = compilation;

                    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
                    {
                        // Only add the field to classes whose name starts with "Target".
                        if (!node.Identifier.ValueText.StartsWith("Target"))
                            return base.VisitClassDeclaration(node);

                        // Idempotent: don't add a second time.
                        if (node.Members.OfType<FieldDeclarationSyntax>().Any(f => f.Declaration.Variables.Any(v => v.Identifier.ValueText == "IsMarked")))
                            return base.VisitClassDeclaration(node);

                        var field = SyntaxFactory.FieldDeclaration(
                            SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName("bool"))
                                .WithVariables(SyntaxFactory.SingletonSeparatedList(
                                    SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier("IsMarked")))))
                            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)));
                        return node.AddMembers(field);
                    }
                }

                public sealed class CountMarkedFields : CSharpSyntaxRewriter
                {
                    private readonly Compilation compilation;
                    private readonly IDictionary<object, object> state;

                    public CountMarkedFields(Compilation compilation, IDictionary<object, object> state)
                    {
                        this.compilation = compilation;
                        this.state = state;
                    }

                    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
                    {
                        // Count types with an IsMarked field across the whole compilation.
                        // This reflects stage 1's edits only because the compilation was refreshed.
                        var markedCount = compilation.GlobalNamespace
                            .GetTypeMembers()
                            .SelectMany(t => t.GetMembers().OfType<IFieldSymbol>())
                            .Count(f => f.Name == "IsMarked");
                        state["MarkedCount"] = markedCount;
                        return base.VisitClassDeclaration(node);
                    }
                }

                public sealed class AnnotateCount : CSharpSyntaxRewriter
                {
                    private readonly IDictionary<object, object> state;

                    public AnnotateCount(IDictionary<object, object> state) => this.state = state;

                    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
                    {
                        // Only annotate the first class in each file (no leading trivia yet).
                        if (node.GetLeadingTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia)))
                            return base.VisitClassDeclaration(node);

                        var count = state.TryGetValue("MarkedCount", out var boxed) ? (int)boxed : 0;
                        var comment = SyntaxFactory.Comment($"// marked: {count}\n");
                        return node.WithLeadingTrivia(comment);
                    }
                }
                """);

            var result = await new RewriteEngine().RunAsync(
                new RewriteRequest([rewriterPath], ["AddMarkerField", "CountMarkedFields", "AnnotateCount"]),
                [fileA, fileB],
                CancellationToken.None);

            Assert.True(result.Converged, "pipeline should converge in a single pass");
            Assert.Equal(1, result.Iterations);

            // Stage 1 added IsMarked to TargetA and TargetB (2 fields total).
            // Stage 2 counted 2 marked fields across the compilation.
            // Stage 3 wrote "// marked: 2" as a leading comment on the first class in each file.
            var textA = Assert.Single(result.FileResults, f => f.FilePath == fileA);
            Assert.Contains("IsMarked", textA.NewText);
            Assert.Contains("// marked: 2", textA.NewText);

            var textB = Assert.Single(result.FileResults, f => f.FilePath == fileB);
            Assert.Contains("IsMarked", textB.NewText);
            Assert.Contains("// marked: 2", textB.NewText);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task State_dictionary_is_preserved_across_stages_and_repeat_until_stable_rounds()
    {
        // The shared state dictionary must survive across:
        //   - stages within a round (stage 1 writes, stage 2 reads), AND
        //   - repeat-until-stable rounds (round 1 writes a counter, round 2 reads it).
        //
        // Stage 1 (BumpCounter) increments state["count"] once per class per round.
        // Stage 2 (EmitCounter) writes the current count as a comment on the class, but only
        // on the first round (so round 2 is the confirming no-change round).
        //
        // With 2 classes in one file and 2 rounds, the counter should reach 4 (2 classes ×
        // 2 rounds) by the time stage 2 runs in round 2 — proving state carried across both
        // stages and rounds.
        var dir = MakeTempDir();
        try
        {
            var targetPath = await WriteTempFileAsync(dir, "Sample.cs", "public class A { public void M() { } }\npublic class B { public void M() { } }\n");
            var rewriterPath = await WriteTempFileAsync(dir, "CounterRewriters.cs", """
                using Microsoft.CodeAnalysis;
                using Microsoft.CodeAnalysis.CSharp;
                using Microsoft.CodeAnalysis.CSharp.Syntax;

                public sealed class BumpCounter : CSharpSyntaxRewriter
                {
                    private readonly IDictionary<object, object> state;

                    public BumpCounter(IDictionary<object, object> state) => this.state = state;

                    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
                    {
                        state["count"] = (state.TryGetValue("count", out var boxed) ? (int)boxed : 0) + 1;
                        return base.VisitClassDeclaration(node);
                    }
                }

                public sealed class EmitCounter : CSharpSyntaxRewriter
                {
                    private readonly IDictionary<object, object> state;

                    public EmitCounter(IDictionary<object, object> state) => this.state = state;

                    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
                    {
                        // Only annotate classes that don't already have a leading comment,
                        // so round 2 (the confirming round) doesn't re-edit and we converge.
                        if (node.GetLeadingTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia)))
                            return base.VisitClassDeclaration(node);

                        var count = state.TryGetValue("count", out var boxed) ? (int)boxed : 0;
                        var comment = SyntaxFactory.Comment($"// count: {count}\n");
                        return node.WithLeadingTrivia(comment);
                    }
                }
                """);

            var result = await new RewriteEngine().RunAsync(
                new RewriteRequest([rewriterPath], ["BumpCounter", "EmitCounter"], repeatUntilStable: true, maxIterations: 10),
                targetPath, CancellationToken.None);

            Assert.True(result.Converged);

            // Stage 1 (BumpCounter) visits class A (count=1) then class B (count=2).
            // Stage 2 (EmitCounter) sees count=2 and writes "// count: 2" on both classes.
            // Round 2: BumpCounter increments again (count=3, 4), EmitCounter sees existing
            // comments and makes no changes → converges.
            Assert.Equal(2, result.Iterations);

            var fileResult = Assert.Single(result.FileResults);
            Assert.Contains("// count: 2", fileResult.NewText);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // -----------------------------------------------------------------------
    // Default expansion excludes nested rewriter classes
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Default_expansion_excludes_nested_rewriter_classes()
    {
        // TopRewriter renames M -> Top; NestedRewriter (declared inside TopRewriter) would rename
        // Top -> Nested. In default mode only the top-level rewriter runs, so the result contains
        // "Top" but never "Nested".
        var dir = MakeTempDir();
        try
        {
            var targetPath = await WriteTempFileAsync(dir, "Sample.cs", "class Sample { public void M() { } }\n");
            var rewriterPath = await WriteTempFileAsync(dir, "WithNested.cs", """
                using Microsoft.CodeAnalysis;
                using Microsoft.CodeAnalysis.CSharp;
                using Microsoft.CodeAnalysis.CSharp.Syntax;

                public sealed class TopRewriter : CSharpSyntaxRewriter
                {
                    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
                        node.Identifier.ValueText == "M"
                            ? node.WithIdentifier(SyntaxFactory.Identifier("Top"))
                            : base.VisitMethodDeclaration(node);

                    public sealed class NestedRewriter : CSharpSyntaxRewriter
                    {
                        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
                            node.Identifier.ValueText == "Top"
                                ? node.WithIdentifier(SyntaxFactory.Identifier("Nested"))
                                : base.VisitMethodDeclaration(node);
                    }
                }
                """);

            var result = await new RewriteEngine().RunAsync(
                new RewriteRequest([rewriterPath]),
                targetPath, CancellationToken.None);

            var fileResult = Assert.Single(result.FileResults);
            Assert.True(fileResult.HasChanges);
            Assert.Contains("Top", fileResult.NewText);
            Assert.DoesNotContain("Nested", fileResult.NewText);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Explicit_type_can_still_select_a_nested_rewriter()
    {
        // An explicit --type fully defines the pipeline and is not subject to the
        // nested-exclusion rule, so naming the nested rewriter runs only it.
        var dir = MakeTempDir();
        try
        {
            var targetPath = await WriteTempFileAsync(dir, "Sample.cs", "class Sample { public void M() { } }\n");
            var rewriterPath = await WriteTempFileAsync(dir, "WithNested.cs", """
                using Microsoft.CodeAnalysis;
                using Microsoft.CodeAnalysis.CSharp;
                using Microsoft.CodeAnalysis.CSharp.Syntax;

                public sealed class TopRewriter : CSharpSyntaxRewriter
                {
                    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
                        node.Identifier.ValueText == "M"
                            ? node.WithIdentifier(SyntaxFactory.Identifier("Top"))
                            : base.VisitMethodDeclaration(node);

                    public sealed class NestedRewriter : CSharpSyntaxRewriter
                    {
                        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
                            node.Identifier.ValueText == "M"
                                ? node.WithIdentifier(SyntaxFactory.Identifier("Nested"))
                                : base.VisitMethodDeclaration(node);
                    }
                }
                """);

            var result = await new RewriteEngine().RunAsync(
                new RewriteRequest([rewriterPath], ["NestedRewriter"]),
                targetPath, CancellationToken.None);

            var fileResult = Assert.Single(result.FileResults);
            Assert.True(fileResult.HasChanges);
            Assert.Contains("Nested", fileResult.NewText);
            Assert.DoesNotContain("Top", fileResult.NewText);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // -----------------------------------------------------------------------
    // EmitDebugSymbols (PDB emission for source-compiled rewriters)
    // -----------------------------------------------------------------------

    private const string ThrowingRewriterSource = """
        using Microsoft.CodeAnalysis;
        using Microsoft.CodeAnalysis.CSharp;
        using Microsoft.CodeAnalysis.CSharp.Syntax;

        public sealed class ThrowingRewriter : CSharpSyntaxRewriter
        {
            public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
            {
                throw new InvalidOperationException("boom");
            }
        }
        """;

    [Fact]
    public async Task EmitDebugSymbols_false_omits_source_line_info_from_stack_trace()
    {
        var dir = MakeTempDir();
        try
        {
            var targetPath = await WriteTempFileAsync(dir, "Sample.cs", "class Sample { public void M() { } }\n");
            var rewriterPath = await WriteTempFileAsync(dir, "ThrowingRewriter.cs", ThrowingRewriterSource);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new RewriteEngine().RunAsync(
                    new RewriteRequest([rewriterPath], ["ThrowingRewriter"], emitDebugSymbols: false),
                    targetPath, CancellationToken.None));

            Assert.Equal("boom", ex.Message);
            // Without a PDB the rewriter's source file does not appear in the stack trace.
            Assert.DoesNotContain("ThrowingRewriter.cs", ex.StackTrace ?? string.Empty);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task EmitDebugSymbols_true_includes_source_file_in_stack_trace()
    {
        var dir = MakeTempDir();
        try
        {
            var targetPath = await WriteTempFileAsync(dir, "Sample.cs", "class Sample { public void M() { } }\n");
            var rewriterPath = await WriteTempFileAsync(dir, "ThrowingRewriter.cs", ThrowingRewriterSource);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new RewriteEngine().RunAsync(
                    new RewriteRequest([rewriterPath], ["ThrowingRewriter"], emitDebugSymbols: true),
                    targetPath, CancellationToken.None));

            Assert.Equal("boom", ex.Message);
            // With a portable PDB the rewriter's source file appears in the stack trace,
            // so a debugger (or assertion) can bind source-level frames.
            Assert.Contains("ThrowingRewriter.cs", ex.StackTrace ?? string.Empty);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
