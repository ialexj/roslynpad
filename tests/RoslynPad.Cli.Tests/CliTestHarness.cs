using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynPad.Cli.Tests;

internal static class CliTestHarness
{
    private static readonly SemaphoreSlim BuildLock = new(1, 1);
    private static string? cliDllPath;

    public static async Task<CliCommandResult> RunCliAsync(params string[] arguments)
    {
        var dllPath = await EnsureCliBuiltAsync();
        return await RunProcessAsync("dotnet", [dllPath, .. arguments], FindRepoRoot());
    }

    public static async Task<CliCommandResult> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        return new CliCommandResult(
            process.ExitCode,
            await standardOutputTask,
            await standardErrorTask,
            fileName,
            [.. arguments]);
    }

    public static DiffCommandResult ParseDiffResult(string standardOutput)
    {
        var result = JsonSerializer.Deserialize<DiffCommandResult>(standardOutput, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        return result ?? throw new InvalidOperationException("CLI diff output could not be parsed.");
    }

    private static async Task<string> EnsureCliBuiltAsync()
    {
        if (cliDllPath is not null)
        {
            return cliDllPath;
        }

        await BuildLock.WaitAsync();
        try
        {
            if (cliDllPath is not null)
            {
                return cliDllPath;
            }

            var repoRoot = FindRepoRoot();
            var cliProjectPath = Path.Combine(repoRoot, "src", "RoslynPad.Cli", "RoslynPad.Cli.csproj");
            var buildResult = await RunProcessAsync("dotnet", ["build", cliProjectPath, "--nologo"], repoRoot);

            Assert.True(buildResult.ExitCode == 0, $"CLI build failed.{Environment.NewLine}{buildResult.FormatForAssertion()}");

            var builtDllPath = Path.Combine(repoRoot, "src", "RoslynPad.Cli", "bin", "Debug", "net10.0", "RoslynPad.Cli.dll");
            Assert.True(File.Exists(builtDllPath), $"Expected CLI output was not found at '{builtDllPath}'.");

            cliDllPath = builtDllPath;
            return cliDllPath;
        }
        finally
        {
            BuildLock.Release();
        }
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "RoslynPad.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the RoslynPad repository root.");
    }
}

internal sealed record CliCommandResult(int ExitCode, string StandardOutput, string StandardError, string FileName, IReadOnlyList<string> Arguments)
{
    public string FormatForAssertion()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Command: {FileName} {string.Join(" ", Arguments)}");
        builder.AppendLine($"ExitCode: {ExitCode}");
        builder.AppendLine("StdOut:");
        builder.AppendLine(StandardOutput);
        builder.AppendLine("StdErr:");
        builder.AppendLine(StandardError);
        return builder.ToString();
    }
}

internal sealed class DiffCommandResult
{
    public IReadOnlyList<DiffFileResult> FileResults { get; init; } = [];
}

internal sealed class DiffFileResult
{
    public string FilePath { get; init; } = string.Empty;

    public string OldText { get; init; } = string.Empty;

    public string NewText { get; init; } = string.Empty;

    public bool HasChanges { get; init; }
}