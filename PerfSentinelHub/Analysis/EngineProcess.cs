using System.Diagnostics;

namespace PerfSentinelHub.Analysis;

/// <summary>
/// Outcome of one engine invocation. <see cref="StandardError"/> is kept for
/// classification only and never reaches a response: ARCH 6.6 bounds what the
/// UI may show to a vocabulary of error codes.
/// </summary>
public sealed record EngineResult(int ExitCode, byte[] StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

public sealed class EngineOutputTooLargeException() : IOException("The engine wrote more than the allowed output.");

public static class EngineProcess
{
    private const int MaxStandardErrorChars = 4096;
    private const int CopyBufferSize = 81_920;

    /// <summary>
    /// Runs the engine and captures its output, killing the whole process tree
    /// when the caller's token trips. Throws <see cref="OperationCanceledException"/>
    /// on cancellation and <see cref="EngineOutputTooLargeException"/> past
    /// <paramref name="maxOutputBytes"/>.
    /// </summary>
    public static async Task<EngineResult> RunAsync(
        string binaryPath,
        IEnumerable<string> arguments,
        long maxOutputBytes,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(binaryPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new IOException($"The engine at {binaryPath} did not start.");
        try
        {
            // Both streams are drained concurrently: a process that fills the
            // stderr pipe while nobody reads it blocks forever on its next write.
            var standardOutput = ReadBoundedAsync(process, maxOutputBytes, cancellationToken);
            var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            var output = await standardOutput;
            var error = await standardError;
            await process.WaitForExitAsync(cancellationToken);
            return new EngineResult(process.ExitCode, output, Truncate(error));
        }
        catch (Exception)
        {
            KillQuietly(process);
            throw;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Process process,
        long maxOutputBytes,
        CancellationToken cancellationToken)
    {
        await using var input = process.StandardOutput.BaseStream;
        using var output = new MemoryStream();
        var buffer = new byte[CopyBufferSize];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return output.ToArray();
            if (output.Length + read > maxOutputBytes)
                throw new EngineOutputTooLargeException();
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static string Truncate(string value) =>
        value.Length <= MaxStandardErrorChars ? value : value[..MaxStandardErrorChars];

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            // Already gone between the check and the kill.
        }
    }
}
