using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynPad.Core;

public sealed class RewriterActivator
{
    public object Create(Type rewriterType, Document document, SemanticModel? semanticModel, Compilation? compilation, ConcurrentDictionary<object, object> state, CancellationToken cancellationToken)
    {
        var constructors = rewriterType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (var constructor in constructors.OrderByDescending(c => c.GetParameters().Length))
        {
            var parameters = constructor.GetParameters();
            var resolved = new object?[parameters.Length];
            var canResolve = true;

            for (var index = 0; index < parameters.Length; index++)
            {
                var parameter = parameters[index];
                if (TryResolve(parameter, document, semanticModel, compilation, state, cancellationToken, out var value))
                {
                    resolved[index] = value;
                }
                else
                {
                    canResolve = false;
                    break;
                }
            }

            if (canResolve)
            {
                return constructor.Invoke(resolved);
            }
        }

        throw new InvalidOperationException($"Unable to resolve a constructor for '{rewriterType.FullName}'.");
    }

    /// <summary>True if the rewriter's constructor asks for a <see cref="SemanticModel"/> or a <see cref="Compilation"/>.</summary>
    /// <remarks>A <see cref="Compilation"/> implies a semantic model (it's the compilation that produces one), so
    /// both parameter types trigger the engine's "recompute compilation before this stage" path.</remarks>
    public static bool RequiresSemanticModel(Type rewriterType) =>
        rewriterType
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(SemanticModel) || p.ParameterType == typeof(Compilation)));

    private static bool TryResolve(ParameterInfo parameter, Document document, SemanticModel? semanticModel, Compilation? compilation, ConcurrentDictionary<object, object> state, CancellationToken cancellationToken, out object? value)
    {
        value = parameter.ParameterType switch
        {
            var type when type == typeof(SemanticModel) => semanticModel,
            var type when type == typeof(Compilation) => compilation,
            var type when type == typeof(Document) => document,
            var type when type == typeof(CancellationToken) => cancellationToken,
            var type when type == typeof(Project) => document.Project,
            var type when type == typeof(SyntaxTree) => document.GetSyntaxTreeAsync().GetAwaiter().GetResult(),
            var type when type == typeof(IDictionary<object, object>) => state,
            _ => null
        };

        return value is not null;
    }
}
