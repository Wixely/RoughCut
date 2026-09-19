using System.Diagnostics;
using System.Text;

namespace RoughCut.Media;

public sealed record ToolResult(byte[] Output, string Error);

public static class ToolProcess
{
    public static async Task<ToolResult> RunAsync(string executable, IEnumerable<string> arguments,
        int outputLimit = 16 * 1024 * 1024, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        lifetime.Token.ThrowIfCancellationRequested();
        process.Start();
        process.StandardInput.Close();
        using var registration = lifetime.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        async Task<byte[]> ReadAsync(Stream stream, int limit)
        {
            try
            {
                using var buffer = new MemoryStream();
                var block = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(block, lifetime.Token)) != 0)
                {
                    if (buffer.Length + count > limit) throw new InvalidDataException("Tool output exceeded its configured limit.");
                    buffer.Write(block, 0, count);
                }
                return buffer.ToArray();
            }
            catch { await lifetime.CancelAsync(); throw; }
        }
        var output = ReadAsync(process.StandardOutput.BaseStream, outputLimit);
        var error = ReadAsync(process.StandardError.BaseStream, 64 * 1024);
        try
        {
            await Task.WhenAll(output, error, process.WaitForExitAsync(lifetime.Token));
            var errorText = Encoding.UTF8.GetString(await error);
            if (process.ExitCode != 0)
                throw new MediaToolException(process.ExitCode, errorText);
            // With -v error, decoding diagnostics indicate an unreliable result even on exit 0.
            if (!string.IsNullOrWhiteSpace(errorText)) throw new MediaToolException(process.ExitCode, errorText);
            return new(await output, errorText);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}

// Retain bounded diagnostics for an explicit local diagnostic view; default CLI output is redacted.
public sealed class MediaToolException(int exitCode, string diagnostic)
    : Exception($"Media tool failed (exit {exitCode}); input may be unsupported or damaged.")
{
    public string Diagnostic { get; } = diagnostic;
}
