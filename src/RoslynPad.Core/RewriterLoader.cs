using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynPad.Core;

public sealed class RewriterLoader
{
    internal RewriterSource Load(string path)
    {
        return IsDll(path) ? LoadFromDll(path) : LoadFromSource(path);
    }

    internal static bool IsDll(string path) =>
        string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase);

    private static RewriterSource LoadFromSource(string sourcePath)
    {
        var sourceText = File.ReadAllText(sourcePath);
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceText, path: sourcePath);
        var diagnostics = syntaxTree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (diagnostics.Count > 0)
        {
            throw new InvalidOperationException($"Rewriter source contains syntax errors: {string.Join(Environment.NewLine, diagnostics.Select(d => d.ToString()))}");
        }

        var references = GetReferences();
        var compilation = CSharpCompilation.Create(
            assemblyName: Path.GetFileNameWithoutExtension(sourcePath),
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        var emitResult = compilation.Emit(stream);
        if (!emitResult.Success)
        {
            throw new InvalidOperationException($"Rewriter compilation failed:{Environment.NewLine}{string.Join(Environment.NewLine, emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()))}");
        }

        stream.Position = 0;
        var alc = new CollectibleAssemblyLoadContext();
        var assembly = alc.LoadFromStream(stream);
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

    private static IReadOnlyList<MetadataReference> GetReferences()
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
