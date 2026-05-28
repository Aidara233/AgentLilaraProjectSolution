using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Plugin.GitTools.Core;

public class GitResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
    public int ExitCode { get; set; }
}

public class GitRunner
{
    private readonly bool _gitAvailable;

    public GitRunner()
    {
        _gitAvailable = CheckGitAvailable();
    }

    public bool IsAvailable => _gitAvailable;

    public Task<GitResult> RunAsync(string workingDir, string args, int timeoutSeconds = 10, CancellationToken ct = default)
    {
        var argList = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return RunAsync(workingDir, argList, timeoutSeconds, ct);
    }

    public async Task<GitResult> RunAsync(string workingDir, IEnumerable<string> args, int timeoutSeconds = 10, CancellationToken ct = default)
    {
        if (!_gitAvailable)
            return new GitResult { Success = false, Error = "git CLI not found. Please install git and ensure it is in PATH." };

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDir,
        };

        psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            using var p = Process.Start(psi);
            if (p == null)
                return new GitResult { Success = false, Error = "Failed to start git process." };

            var outputTask = p.StandardOutput.ReadToEndAsync();
            var errorTask = p.StandardError.ReadToEndAsync();

            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(); } catch { }
                return new GitResult { Success = false, Error = $"git command timed out ({timeoutSeconds}s)." };
            }

            var output = await outputTask;
            var error = await errorTask;

            output = TruncateOutput(output, 10000);

            return new GitResult
            {
                Success = p.ExitCode == 0,
                Output = output,
                Error = error,
                ExitCode = p.ExitCode
            };
        }
        catch (Exception ex)
        {
            return new GitResult { Success = false, Error = $"git execution failed: {ex.Message}" };
        }
    }

    private static string TruncateOutput(string output, int maxLen)
    {
        if (string.IsNullOrEmpty(output) || output.Length <= maxLen) return output;
        return output[..maxLen] + $"\n... (output truncated, total ~{output.Length} chars)";
    }

    private static bool CheckGitAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(5000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }
}
