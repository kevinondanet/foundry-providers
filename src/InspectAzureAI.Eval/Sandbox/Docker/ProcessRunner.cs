using System.ComponentModel;
using System.Diagnostics;
using InspectAzureAI.Eval.Sandbox.Local;

namespace InspectAzureAI.Eval.Sandbox.Docker;

/// <summary>
/// Port of <c>util/_subprocess.py</c> <c>subprocess()</c> for the docker CLI and the local sandbox: stdout and
/// stderr are drained concurrently into tail buffers (a child that fills one pipe while we block on the other
/// would otherwise deadlock), stdin is fed raw bytes, and an expired host timeout kills the whole process tree.
/// </summary>
internal sealed class ProcessRunner : IProcessRunner
{
    /// <summary>
    /// How long the pipes keep being drained once the process has exited or been killed. A descendant that
    /// inherited them (a backgrounded server, a child re-parented away from the killed tree) would otherwise
    /// hold the reads open indefinitely; after this the reads are abandoned and whatever arrived is returned.
    /// </summary>
    internal static readonly TimeSpan DrainGracePeriod = TimeSpan.FromSeconds(2);

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = request.Input is not null,
        };
        if (request.WorkingDirectory is not null)
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (request.Environment is not null)
        {
            foreach (var (name, value) in request.Environment)
            {
                startInfo.Environment[name] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new SandboxUnavailableException($"Could not start '{request.FileName}': {ex.Message}", ex);
        }

        var stdout = new TailByteBuffer(request.OutputLimit);
        var stderr = new TailByteBuffer(request.OutputLimit);
        using var abort = new CancellationTokenSource();
        Task[] pumps =
        [
            PumpAsync(process.StandardOutput.BaseStream, stdout, request, abort),
            PumpAsync(process.StandardError.BaseStream, stderr, request, abort),
            request.Input is { } input ? WriteInputAsync(process, input) : Task.CompletedTask,
        ];

        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, abort.Token);
        if (request.Timeout is { } timeout)
        {
            waitCts.CancelAfter(timeout);
        }

        var timedOut = false;
        var limitExceeded = false;
        try
        {
            await process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            if (cancellationToken.IsCancellationRequested)
            {
                await DrainAsync(pumps).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (abort.IsCancellationRequested)
            {
                limitExceeded = true;
            }
            else
            {
                timedOut = true;
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await DrainAsync(pumps).ConfigureAwait(false);
        var (outBytes, outTotal) = stdout.Snapshot();
        var (errBytes, errTotal) = stderr.Snapshot();
        return new ProcessResult(process.ExitCode, outBytes, errBytes, outTotal, errTotal, timedOut, limitExceeded);
    }

    /// <summary>Waits for the pipe pumps, giving up after <see cref="DrainGracePeriod"/> (the pending reads end on their own when the pipe holder exits).</summary>
    private static async Task DrainAsync(Task[] pumps)
    {
        var all = Task.WhenAll(pumps);
        await Task.WhenAny(all, Task.Delay(DrainGracePeriod)).ConfigureAwait(false);
    }

    private static async Task PumpAsync(Stream stream, TailByteBuffer buffer, ProcessRequest request, CancellationTokenSource abort)
    {
        var chunk = new byte[64 * 1024];
        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(chunk).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The pipe was torn down by a kill; whatever arrived before is the captured output.
                return;
            }

            if (read <= 0)
            {
                return;
            }

            buffer.Write(chunk.AsSpan(0, read));
            if (request.AbortOnOutputLimit && buffer.TotalBytes > request.OutputLimit)
            {
                await abort.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task WriteInputAsync(Process process, ReadOnlyMemory<byte> input)
    {
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(input).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The child exited before consuming stdin (EPIPE); its exit status is the outcome that matters.
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
            }
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }
}
