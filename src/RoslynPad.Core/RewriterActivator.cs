using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynPad.Core;

public sealed class RewriterActivator
{
    public object Create(Type rewriterType, Document document, SemanticModel? semanticModel, CancellationToken cancellationToken)
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
                if (TryResolve(parameter, document, semanticModel, cancellationToken, out var value))
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

    public static bool RequiresSemanticModel(Type rewriterType) =>
        rewriterType
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(SemanticModel)));

    private static bool TryResolve(ParameterInfo parameter, Document document, SemanticModel? semanticModel, CancellationToken cancellationToken, out object? value)
    {
        value = parameter.ParameterType switch
        {
            var type when type == typeof(SemanticModel) => semanticModel,
            var type when type == typeof(Document) => document,
            var type when type == typeof(CancellationToken) => cancellationToken,
            var type when type == typeof(Project) => document.Project,
            var type when type == typeof(SyntaxTree) => document.GetSyntaxTreeAsync().GetAwaiter().GetResult(),
            _ => null
        };

        return value is not null;
    }
}
