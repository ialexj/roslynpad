# roslynpad
Roslynpad is a tool to write .NET Roslyn rewriters and apply them across a whole codebase.

## Command line overview

The current CLI exposes two commands:

- `diff` computes rewrite results and prints them as JSON without modifying files.
- `apply` computes rewrite results and writes changed files back to disk.

Both commands currently support the same flags:

- `--rewriter <path>`: required and repeatable. Points to a rewriter source file (`.cs`) or a precompiled rewriter assembly (`.dll`). When repeated, the values define the default pipeline order.
- `--type <name>`: optional and repeatable. Selects one or more specific rewriter types by simple name or fully qualified name. When present, the `--type` order defines the pipeline order. This flag is required if any `--rewriter` value is a `.dll`.
- `--file <path>`: rewrites a single C# file.
- `--project <path>`: rewrites all discovered C# source files in a `.csproj`.
- `--solution <path>`: rewrites all discovered C# source files in a `.sln`.

Exactly one target flag must be supplied per invocation: `--file`, `--project`, or `--solution`.

Examples:

```powershell
dotnet run --project src/RoslynPad.Cli/RoslynPad.Cli.csproj -- diff \
	--rewriter .\MyRewriter.cs \
	--file .\Sample.cs

dotnet run --project src/RoslynPad.Cli/RoslynPad.Cli.csproj -- apply \
	--rewriter .\MyRewriter.cs \
	--project .\MyProject.csproj
```
