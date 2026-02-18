using System.ComponentModel;
using System.Diagnostics;

namespace SPMResolver.Tool.Services;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public sealed class ProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, string arguments, string? workingDirectory, CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(fileName, workingDirectory);
        startInfo.Arguments = arguments;
        return await RunInternalAsync(startInfo, fileName, arguments, cancellationToken);
    }

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(fileName, workingDirectory);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var formattedArguments = string.Join(" ", arguments.Select(argument => $"\"{argument}\""));
        return await RunInternalAsync(startInfo, fileName, formattedArguments, cancellationToken);
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, string? workingDirectory)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        return startInfo;
    }

    private static async Task<ProcessResult> RunInternalAsync(
        ProcessStartInfo startInfo,
        string fileName,
        string formattedArguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"Failed to start '{fileName}'. Ensure it is installed and available on PATH.", ex);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                catch (Win32Exception)
                {
                }
            }

            throw;
        }

        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Command failed ({process.ExitCode}): {fileName} {formattedArguments}{Environment.NewLine}{error}".Trim());
        }

        return new ProcessResult(process.ExitCode, output, error);
    }
}
