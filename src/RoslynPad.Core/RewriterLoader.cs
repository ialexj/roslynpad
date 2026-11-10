using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;

namespace RoslynPad.Core;

public sealed class RewriterLoader
{
    private const string ImplicitUsings = """
        global using System;
        global using System.Linq;
        global using System.Diagnostics;
        global using System.Collections.Generic;
        global using Microsoft.CodeAnalysis;
        global using Microsoft.CodeAnalysis.CSharp;
        global using Microsoft.CodeAnalysis.CSharp.Syntax;
        """;

    internal RewriterSource Load(string path, bool emitDebugSymbols = false)
    {
        return IsDll(path) ? LoadFromDll(path) : LoadFromSource(path, emitDebugSymbols);
    }

    internal static bool IsDll(string path) =>
        string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase);

    private static RewriterSource LoadFromSource(string sourcePath, bool emitDebugSymbols)
    {
        var sourceText = File.ReadAllText(sourcePath);
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceText, path: sourcePath, encoding: Encoding.UTF8);
        var implicitUsingsTree = CSharpSyntaxTree.ParseText(ImplicitUsings, path: "ImplicitUsings.g.cs", encoding: Encoding.UTF8);
        var diagnostics = syntaxTree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (diagnostics.Count > 0)
        {
            throw new InvalidOperationException($"Rewriter source contains syntax errors: {string.Join(Environment.NewLine, diagnostics.Select(d => d.ToString()))}");
        }

        var references = GetReferences();
        var compilation = CSharpCompilation.Create(
            assemblyName: Path.GetFileNameWithoutExtension(sourcePath),
            syntaxTrees: [implicitUsingsTree, syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var assemblyStream = new MemoryStream();
        using var symbolStream = emitDebugSymbols ? new MemoryStream() : null;
        var emitOptions = emitDebugSymbols
            ? new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb)
            : null;
        var emitResult = compilation.Emit(assemblyStream, symbolStream, options: emitOptions);
        if (!emitResult.Success)
        {
            throw new InvalidOperationException($"Rewriter compilation failed:{Environment.NewLine}{string.Join(Environment.NewLine, emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()))}");
        }

        assemblyStream.Position = 0;
        symbolStream?.Position = 0;
        var alc = new CollectibleAssemblyLoadContext();
        var assembly = symbolStream is null
            ? alc.LoadFromStream(assemblyStream)
            : alc.LoadFromStream(assemblyStream, symbolStream);
        var types = FindCandidateTypes(assembly);

        if (types.Count == 0)
            throw new InvalidOperationException($"No rewriter type was found in '{sourcePath}'.");

        return new RewriterSource(sourcePath, types, alc);
    }

    private static RewriterSource LoadFromDll(string dllPath)
    {
        var bytes = File.ReadAllBytes(dllPath);
        var alc = new CollectibleAssemblyLoadContext();
        var assembly = alc.LoadFromStream(new MemoryStream(bytes));
        var types = FindCandidateTypes(assembly);

        if (types.Count == 0)
            throw new InvalidOperationException($"No rewriter type was found in '{dllPath}'.");

        return new RewriterSource(dllPath, types, alc);
    }

    internal static IReadOnlyList<Type> FindCandidateTypes(System.Reflection.Assembly assembly) =>
        assembly
            .GetTypes()
            .Where(t => typeof(CSharpSyntaxRewriter).IsAssignableFrom(t) && !t.IsAbstract && !t.IsGenericTypeDefinition)
            .ToList();

    /// <summary>Shared framework reference set, also used by RewriteEngine to host loose (non-project) files.</summary>
    internal static IReadOnlyList<MetadataReference> GetReferences()
    {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (trustedPlatformAssemblies is null)
        {
            throw new InvalidOperationException("TRUSTED_PLATFORM_ASSEMBLIES is unavailable.");
        }

        var paths = trustedPlatformAssemblies.Split(Path.PathSeparator);
        var references = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToList();

        references.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(CSharpSyntaxTree).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(CSharpSyntaxRewriter).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(SemanticModel).Assembly.Location));

        return references;
    }
}
