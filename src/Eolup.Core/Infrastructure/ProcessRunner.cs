using System.Diagnostics;
using Eolup.Core;

namespace Eolup.Core.Infrastructure;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Shells out to an external tool (git, dotnet, etc.) and captures its output.</summary>
public static class ProcessRunner
{
    // Generous but bounded: the largest real repo tested (a 28-project monorepo)
    // builds in well under this. Exists purely so a hang becomes a clear,
    // bounded failure instead of an indefinite one — see the node-reuse note
    // below for why a hang can happen at all.
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public static Task<ProcessResult> RunAsync(
        string fileName, string arguments, string workingDirectory, CancellationToken cancellationToken = default) =>
        RunAsync(fileName, new ProcessStartInfo(fileName, arguments), workingDirectory, cancellationToken);

    /// <summary>
    /// Argument-list overload — use this instead of the plain-string overload whenever
    /// an argument's value isn't a literal you wrote yourself (e.g. a PR title/body),
    /// since ArgumentList avoids manual shell-quoting bugs.
    /// </summary>
    public static Task<ProcessResult> RunAsync(
        string fileName, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(fileName);
        foreach (var arg in arguments) startInfo.ArgumentList.Add(arg);
        return RunAsync(fileName, startInfo, workingDirectory, cancellationToken);
    }

    private static async Task<ProcessResult> RunAsync(
        string fileName, ProcessStartInfo startInfo, string workingDirectory, CancellationToken cancellationToken)
    {
        startInfo.WorkingDirectory = workingDirectory;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;

        // MSBuild defaults to keeping background worker processes alive between
        // invocations ("node reuse") to speed up subsequent builds. Found as a
        // real, intermittent CI hang: a lingering worker process can inherit our
        // redirected stdout/stderr handles, so ReadToEndAsync below waits
        // forever for an EOF that never comes — because something other than
        // our direct child is still holding the pipe open. This showed up as a
        // CI job stuck on "Test" for 10+ minutes on one run, then completing
        // normally in under 2 on the very next one — a timing-dependent hang,
        // not a deterministic failure, which is exactly the profile of this
        // class of bug. Disabling node reuse stops Eolup's own dotnet
        // invocations from leaving anything behind that could hold the pipe.
        startInfo.EnvironmentVariables["MSBUILDDISABLENODEREUSE"] = "1";

        // Compiler servers (VBCSCompiler) are shared, long-lived background
        // processes a build can spawn; like MSBuild worker nodes they can inherit our
        // redirected stdout/stderr and keep the pipe open after the build itself has
        // finished. Our own builds have no use for a warm compiler server.
        startInfo.EnvironmentVariables["UseSharedCompilation"] = "false";

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Output is collected chunk by chunk into buffers we own, rather than with
        // ReadToEndAsync, so that if the pipe never reaches EOF we still hold
        // everything the process wrote up to that point (see the grace period below).
        var stdOut = new OutputBuffer();
        var stdErr = new OutputBuffer();
        var stdOutPump = stdOut.PumpAsync(process.StandardOutput);
        var stdErrPump = stdErr.PumpAsync(process.StandardError);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(DefaultTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timed out, not caller-cancelled: kill the whole process tree and fail
            // loud with a clear, bounded error rather than let CI hang.
            TryKill(process);
            throw new EolupUserException(
                $"'{fileName}' did not complete within {DefaultTimeout.TotalMinutes:0} minutes and was killed. " +
                "This may indicate a lingering background process (e.g. an MSBuild/compiler server) holding a pipe open.");
        }

        // The process has exited, but its output pipes only reach EOF once *every*
        // process that inherited them has closed them. A leftover background process
        // can hold them open indefinitely — found migrating TaskoMask: the CLI sat
        // for 20+ minutes after the build had finished, with the 5-minute timeout
        // above never applying, because it only guarded the wait for exit. So give
        // the pipes a bounded moment to drain, then carry on with what we have.
        var drained = Task.WhenAll(stdOutPump, stdErrPump);
        await Task.WhenAny(drained, Task.Delay(OutputDrainGrace, cancellationToken));

        return new ProcessResult(process.ExitCode, stdOut.Snapshot(), stdErr.Snapshot());
    }

    /// <summary>
    /// How long to keep waiting for a finished process's output pipes to close before
    /// giving up on them and using the output captured so far. Internal so tests can
    /// shorten it.
    /// </summary>
    internal static TimeSpan OutputDrainGrace { get; set; } = TimeSpan.FromSeconds(30);

    private sealed class OutputBuffer
    {
        private readonly System.Text.StringBuilder _text = new();
        private readonly object _lock = new();

        public async Task PumpAsync(StreamReader reader)
        {
            var chunk = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(chunk, 0, chunk.Length)) > 0)
            {
                lock (_lock) _text.Append(chunk, 0, read);
            }
        }

        public string Snapshot()
        {
            lock (_lock) return _text.ToString();
        }
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch { /* best effort — the process may already be gone */ }
    }
}
