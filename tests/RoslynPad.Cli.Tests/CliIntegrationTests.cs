namespace RoslynPad.Cli.Tests;

public sealed class CliIntegrationTests
{
    [Fact]
    public async Task Diff_file_outputs_rewritten_json_and_keeps_source_unchanged()
    {
        await using var workspace = CliTestWorkspace.Create();
        var targetPath = await workspace.WriteClassAsync("Sample.cs", "Sample");
        var rewriterPath = await workspace.WriteFileAsync("RenameMethodRewriter.cs", RewriterUtil.RenameMethodRewriter);

        var result = await CliTestHarness.RunCliAsync("diff", "--rewriter", rewriterPath, "--file", targetPath);

        Assert.True(result.ExitCode == 0, result.FormatForAssertion());

        var diff = CliTestHarness.ParseDiffResult(result.StandardOutput);
        var fileResult = Assert.Single(diff.FileResults);
        Assert.Equal(Path.GetFullPath(targetPath), fileResult.FilePath);
        Assert.True(fileResult.HasChanges);
        Assert.Contains("Renamed", fileResult.NewText);
        Assert.DoesNotContain("Renamed", await File.ReadAllTextAsync(targetPath));
    }

    [Fact]
    public async Task Diff_with_detailed_exit_code_returns_1_when_no_files_change()
    {
        await using var workspace = CliTestWorkspace.Create();
        var targetPath = await workspace.WriteClassAsync("Sample.cs", "Sample");
        var rewriterPath = await workspace.WriteFileAsync("NoOpRewriter.cs", RewriterUtil.Rewriter("NoOpRewriter", """
            public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
                base.VisitMethodDeclaration(node);
            """));

        var result = await CliTestHarness.RunCliAsync("diff", "--detailed-exit-code", "--rewriter", rewriterPath, "--file", targetPath);

        Assert.Equal(1, result.ExitCode);
        var diff = CliTestHarness.ParseDiffResult(result.StandardOutput);
        Assert.Empty(diff.FileResults);
    }

    [Fact]
    public async Task Apply_file_writes_the_rewritten_text_to_disk()
    {
        await using var workspace = CliTestWorkspace.Create();
        var targetPath = await workspace.WriteClassAsync("Sample.cs", "Sample");
        var rewriterPath = await workspace.WriteFileAsync("RenameMethodRewriter.cs", RewriterUtil.Rewriter("RenameMethodRewriter", """
            public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
                node.WithIdentifier(Identifier("Applied"));
            """));

        var result = await CliTestHarness.RunCliAsync("apply", "--rewriter", rewriterPath, "--file", targetPath);

        Assert.True(result.ExitCode == 0, result.FormatForAssertion());
        Assert.Contains("Applied", await File.ReadAllTextAsync(targetPath));
        Assert.DoesNotContain("void M()", await File.ReadAllTextAsync(targetPath));
    }

    [Fact]
    public async Task Apply_project_rewrites_project_documents_without_touching_bin_or_obj_files()
    {
        await using var workspace = CliTestWorkspace.Create();
        var projectPath = await workspace.WriteProjectAsync("ProjectUnderTest");
        var fileAPath = await workspace.WriteClassAsync("ProjectUnderTest/A.cs", "A");
        var fileBPath = await workspace.WriteClassAsync("ProjectUnderTest/B.cs", "B");
        var binPath = await workspace.WriteClassAsync("ProjectUnderTest/bin/Debug/net10.0/BinOnly.cs", "BinOnly");
        var objPath = await workspace.WriteClassAsync("ProjectUnderTest/obj/Debug/net10.0/ObjOnly.cs", "ObjOnly");
        var rewriterPath = await workspace.WriteFileAsync("ProjectRenameRewriter.cs", RewriterUtil.Rewriter("ProjectRenameRewriter", """
            public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
                node.WithIdentifier(Identifier("ProjectRenamed"));
            """));

        await workspace.RestoreAsync(projectPath);

        var result = await CliTestHarness.RunCliAsync("apply", "--rewriter", rewriterPath, "--project", projectPath);

        Assert.True(result.ExitCode == 0, result.FormatForAssertion());
        Assert.Contains("ProjectRenamed", await File.ReadAllTextAsync(fileAPath));
        Assert.Contains("ProjectRenamed", await File.ReadAllTextAsync(fileBPath));
        Assert.DoesNotContain("ProjectRenamed", await File.ReadAllTextAsync(binPath));
        Assert.DoesNotContain("ProjectRenamed", await File.ReadAllTextAsync(objPath));
    }

    [Fact]
    public async Task Apply_solution_rewrites_documents_across_multiple_projects()
    {
        await using var workspace = CliTestWorkspace.Create();
        var firstProjectPath = await workspace.WriteProjectAsync("FirstProject");
        var secondProjectPath = await workspace.WriteProjectAsync("SecondProject");
        var firstFilePath = await workspace.WriteClassAsync("FirstProject/First.cs", "First");
        var secondFilePath = await workspace.WriteClassAsync("SecondProject/Second.cs", "Second");
        var rewriterPath = await workspace.WriteFileAsync("SolutionRenameRewriter.cs", RewriterUtil.Rewriter("SolutionRenameRewriter", """
            public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
                node.WithIdentifier(Identifier("SolutionRenamed"));
            """));

        var solutionPath = await workspace.CreateSolutionAsync("Workspace", firstProjectPath, secondProjectPath);
        await workspace.RestoreAsync(solutionPath);

        var result = await CliTestHarness.RunCliAsync("apply", "--rewriter", rewriterPath, "--solution", solutionPath);

        Assert.True(result.ExitCode == 0, result.FormatForAssertion());
        Assert.Contains("SolutionRenamed", await File.ReadAllTextAsync(firstFilePath));
        Assert.Contains("SolutionRenamed", await File.ReadAllTextAsync(secondFilePath));
    }

    [Fact]
    public async Task Apply_honors_rewriter_option_order_when_multiple_rewriter_files_are_provided()
    {
        await using var workspace = CliTestWorkspace.Create();
        var targetPath = await workspace.WriteClassAsync("Sample.cs", "Sample");
        var firstRewriterPath = await workspace.WriteFileAsync("FirstRewriter.cs", RewriterUtil.Rewriter("FirstRewriter", """
            public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
                node.Identifier.ValueText == "M"
                    ? node.WithIdentifier(Identifier("Intermediate"))
                    : base.VisitMethodDeclaration(node);
            """));
        var secondRewriterPath = await workspace.WriteFileAsync("SecondRewriter.cs", RewriterUtil.Rewriter("SecondRewriter", """
            public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
                node.Identifier.ValueText == "Intermediate"
                    ? node.WithIdentifier(Identifier("Final"))
                    : base.VisitMethodDeclaration(node);
            """));

        var result = await CliTestHarness.RunCliAsync(
            "apply",
            "--rewriter", firstRewriterPath,
            "--rewriter", secondRewriterPath,
            "--file", targetPath);

        Assert.True(result.ExitCode == 0, result.FormatForAssertion());
        var rewrittenText = await File.ReadAllTextAsync(targetPath);
        Assert.Contains("Final", rewrittenText);
        Assert.DoesNotContain("Intermediate", rewrittenText);
    }

    [Fact]
    public async Task Apply_honors_explicit_type_order_across_multiple_rewriter_files()
    {
        await using var workspace = CliTestWorkspace.Create();
        var targetPath = await workspace.WriteClassAsync("Sample.cs", "Sample");
        var suffixRewriterPath = await workspace.WriteFileAsync("SuffixRewriter.cs", RewriterUtil.Rewriter("AddSuffix", """
            public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
                node.WithIdentifier(Identifier(node.Identifier.ValueText + "_A"));
            """));
        var prefixRewriterPath = await workspace.WriteFileAsync("PrefixRewriter.cs", RewriterUtil.Rewriter("AddPrefix", """
            public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
                node.WithIdentifier(Identifier("B_" + node.Identifier.ValueText));
            """));

        var result = await CliTestHarness.RunCliAsync(
            "apply",
            "--rewriter", suffixRewriterPath,
            "--rewriter", prefixRewriterPath,
            "--type", "AddPrefix",
            "--type", "AddSuffix",
            "--file", targetPath);

        Assert.True(result.ExitCode == 0, result.FormatForAssertion());
        Assert.Contains("B_M_A", await File.ReadAllTextAsync(targetPath));
    }

    [Fact]
    public async Task Apply_supports_compiled_rewriter_assemblies_when_type_is_specified()
    {
        await using var workspace = CliTestWorkspace.Create();
        var targetPath = await workspace.WriteClassAsync("Sample.cs", "Sample");
        var compiledRenamePath = await workspace.WriteFileAsync("CompiledRename.cs", RewriterUtil.Rewriter("CompiledRename", """
            public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
                node.WithIdentifier(Identifier("Compiled"));
            """));
        var unusedRewriterPath = await workspace.WriteFileAsync("UnusedRewriter.cs", RewriterUtil.Rewriter("UnusedRewriter", """
            public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
                node.WithIdentifier(Identifier("Ignored"));
            """));
        var rewriterAssemblyPath = await workspace.CompileToDllAsync("CompiledRewriters", compiledRenamePath, unusedRewriterPath);

        var result = await CliTestHarness.RunCliAsync(
            "apply",
            "--rewriter", rewriterAssemblyPath,
            "--type", "CompiledRename",
            "--file", targetPath);

        Assert.True(result.ExitCode == 0, result.FormatForAssertion());
        var rewrittenText = await File.ReadAllTextAsync(targetPath);
        Assert.Contains("Compiled", rewrittenText);
        Assert.DoesNotContain("Ignored", rewrittenText);
    }

    // -----------------------------------------------------------------------
    // --repeat-until-stable / --max-iterations
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Apply_repeat_until_stable_converges_and_writes_final_stabilized_text()
    {
        await using var workspace = CliTestWorkspace.Create();
        var targetPath = await workspace.WriteClassAsync("Sample.cs", "Sample");
        var rewriterPath = await workspace.WriteFileAsync("IncrementRewriter.cs", RewriterUtil.Rewriter("IncrementRewriter", """
            public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) {
                var name = node.Identifier.ValueText;
                var underscoreIndex = name.IndexOf('_');
                var count = underscoreIndex < 0 ? 0 : int.Parse(name[(underscoreIndex + 1)..]);
                if (count >= 2)
                    return base.VisitMethodDeclaration(node);
                var baseName = underscoreIndex < 0 ? name : name[..underscoreIndex];
                return node.WithIdentifier(Identifier($"{baseName}_{count + 1}"));
            }
            """));

        var result = await CliTestHarness.RunCliAsync(
            "apply", "--rewriter", rewriterPath, "--file", targetPath,
            "--repeat-until-stable", "--max-iterations", "10", "--detailed-exit-code");

        Assert.True(result.ExitCode == 0, result.FormatForAssertion());
        Assert.Contains("M_2", await File.ReadAllTextAsync(targetPath));
    }

    [Fact]
    public async Task Diff_repeat_until_stable_returns_exit_code_3_when_max_iterations_reached_without_converging()
    {
        await using var workspace = CliTestWorkspace.Create();
        var targetPath = await workspace.WriteClassAsync("Sample.cs", "Sample");
        var rewriterPath = await workspace.WriteFileAsync("ToggleRewriter.cs", RewriterUtil.Rewriter("ToggleRewriter", """
            public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
                node.WithIdentifier(Identifier(node.Identifier.ValueText == "A" ? "B" : "A"));
            """));
        var originalText = await File.ReadAllTextAsync(targetPath);

        var result = await CliTestHarness.RunCliAsync(
            "diff", "--rewriter", rewriterPath, "--file", targetPath,
            "--repeat-until-stable", "--max-iterations", "3", "--detailed-exit-code");

        Assert.Equal(3, result.ExitCode);
        Assert.Equal(originalText, await File.ReadAllTextAsync(targetPath)); // diff never touches disk
    }

    [Fact]
    public async Task Apply_repeat_until_stable_writes_best_effort_result_when_non_convergent()
    {
        await using var workspace = CliTestWorkspace.Create();
        var targetPath = await workspace.WriteClassAsync("Sample.cs", "Sample");
        var rewriterPath = await workspace.WriteFileAsync("ToggleRewriter.cs", RewriterUtil.Rewriter("ToggleRewriter", """
            public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
                node.WithIdentifier(Identifier(node.Identifier.ValueText == "A" ? "B" : "A"));
            """));

        var result = await CliTestHarness.RunCliAsync(
            "apply", "--rewriter", rewriterPath, "--file", targetPath,
            "--repeat-until-stable", "--max-iterations", "3", "--detailed-exit-code");

        Assert.Equal(3, result.ExitCode);
        // Non-convergent, but the last round's best-effort output (round 3 lands on "A") is still written.
        Assert.Contains("public void A()", await File.ReadAllTextAsync(targetPath));
    }

    // -----------------------------------------------------------------------
    // Multi-rewriter pipeline: per-stage (not per-file) processing
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Apply_pipeline_lets_stage_one_change_be_visible_to_stage_two_in_the_same_pass()
    {
        // Stage 1 (RenameHelper) renames Helper.M -> Ready in Helper.cs.
        // Stage 2 (ConfirmWhenReady) renames Consumer.M -> Confirmed in Consumer.cs, but ONLY if it
        // can see Helper.Ready via the semantic model. Under per-stage processing, stage 1's edit
        // is folded into the Solution before stage 2 runs, so stage 2 sees Helper.Ready in a single
        // pass — no --repeat-until-stable needed.
        await using var workspace = CliTestWorkspace.Create();
        var projectPath = await workspace.WriteProjectAsync("ProjectUnderTest");
        var helperPath = await workspace.WriteFileAsync("ProjectUnderTest/Helper.cs", "public class Helper { public void M() { } }\n");
        var consumerPath = await workspace.WriteFileAsync("ProjectUnderTest/Consumer.cs", "public class Consumer { public void M() { } }\n");
        var rewriterPath = await workspace.WriteFileAsync("PipelineRewriters.cs", """
            using System.Linq;
            using Microsoft.CodeAnalysis;
            using Microsoft.CodeAnalysis.CSharp;
            using Microsoft.CodeAnalysis.CSharp.Syntax;
            using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

            public sealed class RenameHelper : CSharpSyntaxRewriter {
                public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) {
                    var containingClass = node.Parent as ClassDeclarationSyntax;
                    return containingClass?.Identifier.ValueText == "Helper" && node.Identifier.ValueText == "M"
                        ? node.WithIdentifier(Identifier("Ready"))
                        : base.VisitMethodDeclaration(node);
                }
            }

            public sealed class ConfirmWhenReady : CSharpSyntaxRewriter {
                private readonly SemanticModel semanticModel;
                public ConfirmWhenReady(SemanticModel semanticModel) => this.semanticModel = semanticModel;

                public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) {
                    var containingClass = node.Parent as ClassDeclarationSyntax;
                    if (containingClass?.Identifier.ValueText == "Consumer" && node.Identifier.ValueText == "M") {
                        var helperType = semanticModel.Compilation.GetTypeByMetadataName("Helper");
                        var isReady = helperType?.GetMembers("Ready").Any() == true;
                        return isReady ? node.WithIdentifier(Identifier("Confirmed")) : base.VisitMethodDeclaration(node);
                    }
                    return base.VisitMethodDeclaration(node);
                }
            }
            """);

        await workspace.RestoreAsync(projectPath);

        var result = await CliTestHarness.RunCliAsync(
            "apply",
            "--rewriter", rewriterPath,
            "--type", "RenameHelper",
            "--type", "ConfirmWhenReady",
            "--project", projectPath);

        Assert.True(result.ExitCode == 0, result.FormatForAssertion());
        Assert.Contains("Ready", await File.ReadAllTextAsync(helperPath));
        Assert.Contains("Confirmed", await File.ReadAllTextAsync(consumerPath));
    }

}