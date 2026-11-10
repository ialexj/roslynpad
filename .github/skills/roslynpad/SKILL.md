---
name: roslynpad
description: 'Use RoslynPad CLI to author and execute C# syntax rewriters for repeatable code transformations across files, projects, or solutions. Covers CSharpSyntaxRewriter, semantic models, ordered pipelines, JSON diff previews, safe apply, and repeat-until-stable execution.'
---

# RoslynPad

RoslynPad compiles C# rewriter source or loads precompiled rewriter assemblies,
then executes an ordered pipeline against C# documents. Use it for repeatable,
syntax-aware or semantic-aware transformations rather than text replacement.

## 1. Discover the CLI and select a target

- If installed, run `roslynpad --help`, then `roslynpad diff --help` or
  `roslynpad apply --help` for the current supported options.
- From this repository, use
  `dotnet run --project src\RoslynPad.Cli\RoslynPad.Cli.csproj -- --help`.
  Replace `roslynpad` in the examples below with that command prefix, excluding
  its final `--help`, when running from source.
- Specify exactly one of `--file <path.cs>`, `--project <path.csproj>`, or
  `--solution <path.sln>`.
- Prefer a project or solution target for transformations requiring actual
  project references, compiler options, and cross-file semantic information.
  A loose file uses a synthetic project with framework references, not its
  containing project's context.
- Project and solution targets use their C# documents, excluding `bin` and `obj`
  directories. Keep rewriter source outside target project compilation inputs.

Do not assume planned options exist. In particular, the current CLI has no
`--include`, `--exclude`, `--only`, or `--format` options.

## 2. Author a trusted rewriter

Create a concrete, nongeneric class derived from
`Microsoft.CodeAnalysis.CSharp.CSharpSyntaxRewriter`. Do not use
`CSharpSyntaxWalker` as a rewrite stage. Rewriters execute in-process with the
agent's permissions; inspect and trust source files and DLLs before loading them.

Example: rename a specific method declaration syntactically:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

public sealed class RenameMethod : CSharpSyntaxRewriter
{
    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
        return node.Identifier.ValueText == "OldName"
            ? visited.WithIdentifier(
                SyntaxFactory.Identifier("NewName").WithTriviaFrom(visited.Identifier))
            : visited;
    }
}
```

This example changes declarations only, not references. For symbol-sensitive
changes, compare symbols using a semantic model and handle the relevant
declarations and references explicitly; RoslynPad is not an automatic symbol
rename service. Preserve comments and whitespace trivia in replacements.

Constructor parameters can request the following exact service types:

- `SemanticModel`, `Compilation`, `Document`, `Project`, or `SyntaxTree`
  from `Microsoft.CodeAnalysis`.
- `System.Threading.CancellationToken`.
- `System.Collections.Generic.IDictionary<object, object>` for shared run state.

Use semantic queries on nodes from the incoming tree before constructing
replacement nodes. New nodes do not belong to that semantic model.

## 3. Define the pipeline

- Pass each input as `--rewriter <source.cs>` or `--rewriter <assembly.dll>`.
  Repeat the option for multiple inputs.
- Without `--type`, source inputs expand to their top-level rewriter classes in
  declaration order, following the order of `--rewriter` occurrences.
- With any `--type`, the ordered list of `--type` values completely defines the
  pipeline. Inputs supply available types, not implicit additional stages.
  Prefer fully qualified names to avoid ambiguity.
- A DLL input requires explicit `--type` selection.
- Repeating an input or a selected type is supported.
- Each stage processes all target documents before the next stage; later stages
  see earlier stages' edits, including through refreshed semantic models.
  Documents within a stage are processed in parallel against that stage's
  snapshot. Do not rely on document visitation order.

## 4. Preview, review, then apply

```powershell
roslynpad diff --rewriter .\rewriters\RenameMethod.cs --file .\Sample.cs --detailed-exit-code
roslynpad apply --rewriter .\rewriters\RenameMethod.cs --file .\Sample.cs --detailed-exit-code
```

Run the second command only after reviewing the first command's result.

- `diff` never writes target files. Its stdout is JSON with `FileResults`,
  `HasAnyChanges`, `Converged`, and `Iterations`.
- `FileResults` contains only changed files, each with `FilePath`, `OldText`,
  `NewText`, and `HasChanges`. An empty list means no changes.
- Errors are reported on stderr. When using `dotnet run` as a JSON-producing
  command, build first and add `--no-build` before the `--` separator so build
  messages do not contaminate stdout.
- Review every changed file for intended scope, correctness, and trivia changes.
- `apply` reruns the pipeline and writes changed files; it does not consume JSON
  from a previous preview. Keep source, target files, and arguments unchanged
  between preview and apply, and make rewriters deterministic.

For iterative transformations, pass `--repeat-until-stable` and optionally
`--max-iterations <positive-integer>` to both commands. The default cap is 10.
Each round runs the full pipeline on the prior in-memory result. Convergence
requires an entire round with no changes. Inspect `Converged` before applying:
`apply` can write the final result even when the iteration cap is reached.

With `--detailed-exit-code`:

| Code | Meaning |
|---|---|
| 0 | Completed with changes |
| 1 | Completed with no changes |
| 2 | Rewrite, load, or target error |
| 3 | Repeat-until-stable did not converge |

These are execution-result codes; argument parsing failures must also be checked
through stderr. Without the flag, successful execution returns 0 even with no
changes; errors and nonconvergence return 1. Each invocation is independent.
Avoid `--wait-for-debugger` in unattended runs because it blocks for attachment.

## 5. Verify the transformation

After applying, review the actual repository diff and run the affected project's
build and focused tests. Check that unrelated files are untouched. For a
transformation intended to be idempotent, run `diff` again and expect empty
`FileResults`. Report changed scope, verification results, and any errors or
nonconvergence; never treat an exit code alone as proof of correctness.
