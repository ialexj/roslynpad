namespace RoslynPad.Cli.Tests;

public static class RewriterUtil
{
    public static string Rewriter(string typeName, string members)
        => $$"""
            using Microsoft.CodeAnalysis;
            using Microsoft.CodeAnalysis.CSharp;
            using Microsoft.CodeAnalysis.CSharp.Syntax;
            using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

            public sealed class {{typeName}} : CSharpSyntaxRewriter {
                {{members}}
            }
            """;

    public static readonly string RenameMethodRewriter = Rewriter("RenameMethodRewriter", """
        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) {
            return node.Identifier.ValueText == "M"
                ? node.WithIdentifier(SyntaxFactory.Identifier("Renamed"))
                : base.VisitMethodDeclaration(node);
        }
        """);
}