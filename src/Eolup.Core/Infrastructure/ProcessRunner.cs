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
    // What the quick commands get: git, gh, MSBuild property evaluation. Generous for those, and it
    // exists purely so a hang becomes a clear, bounded failure instead of an indefinite one — see the
    // node-reuse note below for why a hang can happen at all. A repo's own restore, build and test run
    // are a different matter: they legitimately take tens of minutes, so their callers pass their own
    // (configurable) limit to the overload that takes one.
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    public static Task<ProcessResult> RunAsync(
        string fileName, string arguments, string workingDirectory, CancellationToken cancellationToken = default) =>
        RunAsync(fileName, new ProcessStartInfo(fileName, arguments), workingDirectory, DefaultTimeout, cancellationToken);

    /// <summary>
    /// Argument-list overload — use this instead of the plain-string overload whenever
    /// an argument's value isn't a literal you wrote yourself (e.g. a PR title/body),
    /// since ArgumentList avoids manual shell-quoting bugs.
    /// </summary>
    public static Task<ProcessResult> RunAsync(
        string fileName, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken = default) =>
        RunAsync(fileName, arguments, workingDirectory, DefaultTimeout, cancellationToken);

    /// <summary>
    /// As above, with the process's own time limit. A process still running when it expires is killed with its
    /// whole process tree and a <see cref="ProcessTimeoutException"/> carrying what it had written so far is thrown.
    /// </summary>
    public static Task<ProcessResult> RunAsync(
        string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(fileName);
        foreach (var arg in arguments) startInfo.ArgumentList.Add(arg);
        return RunAsync(fileName, startInfo, workingDirectory, timeout, cancellationToken);
    }

    private static async Task<ProcessResult> RunAsync(
        string fileName, ProcessStartInfo startInfo, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
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
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Whoever stopped the wait (the caller, or the time limit), nothing may keep running: kill the whole
            // process tree. A caller that gives up used to leave its `dotnet build` running behind it.
            TryKill(process);

            if (cancellationToken.IsCancellationRequested)
                throw; // cancelled by the caller: nothing more to say

            // Timed out: fail loud with what was running, how long it had, and what it had written — never a bare "hung".
            await Task.WhenAny(Task.WhenAll(stdOutPump, stdErrPump), Task.Delay(TimeoutOutputGrace, CancellationToken.None));
            throw new ProcessTimeoutException(Describe(fileName, startInfo), timeout, stdOut.Snapshot() + stdErr.Snapshot());
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

    /// <summary>How long to wait, after killing a timed-out process, for its pipes to hand over the last of its output.</summary>
    private static readonly TimeSpan TimeoutOutputGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long to keep waiting for a finished process's output pipes to close before
    /// giving up on them and using the output captured so far. Internal so tests can
    /// shorten it.
    /// </summary>
    internal static TimeSpan OutputDrainGrace { get; set; } = TimeSpan.FromSeconds(30);

    private static string Describe(string fileName, ProcessStartInfo startInfo) =>
        Describe(fileName, startInfo.ArgumentList.Count > 0
            ? startInfo.ArgumentList[0]
            : startInfo.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());

    /// <summary>
    /// The program and its subcommand, as a person would say it: "dotnet test", "git checkout". A first argument that
    /// is an option ("-c", "/c", "--version") isn't a subcommand, and neither is anything long and path-like, so
    /// those are left out rather than echoed into a message.
    /// </summary>
    internal static string Describe(string fileName, string? firstArgument)
    {
        var program = Path.GetFileNameWithoutExtension(fileName);
        return firstArgument is { Length: > 0 and <= 24 } && firstArgument[0] is not ('-' or '/') && firstArgument.All(c => char.IsLetterOrDigit(c) || c == '-')
            ? $"{program} {firstArgument}"
            : program;
    }

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
