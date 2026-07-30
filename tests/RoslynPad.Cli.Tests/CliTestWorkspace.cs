using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace RoslynPad.Cli.Tests;

internal sealed class CliTestWorkspace : IAsyncDisposable
{
    private CliTestWorkspace(string rootPath)
    {
        RootPath = rootPath;
    }

    public string RootPath { get; }

    public static CliTestWorkspace Create()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "roslynpad-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        return new CliTestWorkspace(rootPath);
    }

    public async Task<string> WriteFileAsync(string relativePath, string content)
    {
        var path = Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, content);
        return path;
    }

    public Task<string> WriteClassAsync(string relativePath, string className)
    {
        return WriteFileAsync(relativePath, $"class {className} {{ public void M() {{ }} }}\n");
    }

    public async Task<string> CompileToDllAsync(string assemblyName, params string[] sourceFilePaths)
    {
        var syntaxTrees = new List<SyntaxTree>(sourceFilePaths.Length);
        foreach (var sourceFilePath in sourceFilePaths)
        {
            syntaxTrees.Add(CSharpSyntaxTree.ParseText(await File.ReadAllTextAsync(sourceFilePath), path: sourceFilePath));
        }

        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("TRUSTED_PLATFORM_ASSEMBLIES unavailable.");

        var references = trustedPlatformAssemblies
            .Split(Path.PathSeparator)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => MetadataReference.CreateFromFile(path))
            .Cast<MetadataReference>()
            .Append(MetadataReference.CreateFromFile(typeof(CSharpSyntaxRewriter).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(SemanticModel).Assembly.Location))
            .GroupBy(reference => reference.Display, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        var compilation = CSharpCompilation.Create(
            assemblyName,
            syntaxTrees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var dllPath = Path.Combine(RootPath, assemblyName + ".dll");
        await using var stream = File.Create(dllPath);
        var result = compilation.Emit(stream);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
        return dllPath;
    }

    public Task<string> WriteProjectAsync(string projectName)
    {
        return WriteFileAsync($"{projectName}/{projectName}.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
    }

    public async Task<string> CreateSolutionAsync(string solutionName, params string[] projectPaths)
    {
        var createResult = await CliTestHarness.RunProcessAsync("dotnet", ["new", "sln", "--name", solutionName, "--format", "sln"], RootPath);
        Assert.True(createResult.ExitCode == 0, $"Failed to create solution.{Environment.NewLine}{createResult.FormatForAssertion()}");

        var solutionPath = Path.Combine(RootPath, solutionName + ".sln");
        foreach (var projectPath in projectPaths)
        {
            var addResult = await CliTestHarness.RunProcessAsync("dotnet", ["sln", solutionPath, "add", projectPath], RootPath);
            Assert.True(addResult.ExitCode == 0, $"Failed to add project '{projectPath}' to solution.{Environment.NewLine}{addResult.FormatForAssertion()}");
        }

        return solutionPath;
    }

    public async Task RestoreAsync(string path)
    {
        var restoreResult = await CliTestHarness.RunProcessAsync("dotnet", ["restore", path, "--nologo"], RootPath);
        Assert.True(restoreResult.ExitCode == 0, $"dotnet restore failed for '{path}'.{Environment.NewLine}{restoreResult.FormatForAssertion()}");
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}
