using System.Runtime.Loader;

namespace RoslynPad.Core;

internal sealed class RewriterSource(string path, IReadOnlyList<Type> types, AssemblyLoadContext assemblyLoadContext)
{
    public string Path { get; } = path;
    public IReadOnlyList<Type> Types { get; } = types;
    public AssemblyLoadContext AssemblyLoadContext { get; } = assemblyLoadContext;
}
