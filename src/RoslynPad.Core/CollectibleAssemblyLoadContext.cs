using System.Reflection;
using System.Runtime.Loader;

namespace RoslynPad.Core;

public sealed class CollectibleAssemblyLoadContext : AssemblyLoadContext
{
    public CollectibleAssemblyLoadContext()
        : base(isCollectible: true)
    {
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        return null;
    }
}
