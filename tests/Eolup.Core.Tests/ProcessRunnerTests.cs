using System.Diagnostics;
using Eolup.Core.Infrastructure;
using Xunit;

namespace Eolup.Core.Tests;

public class ProcessRunnerTests
{
    [Fact]
    public async Task ReturnsPromptlyWithCapturedOutput_WhenABackgroundChildKeepsThePipeOpen()
    {
        // Regression: the 5-minute timeout only guarded the wait for the process to
        // EXIT. Once it had exited, reading its output waited for the pipe to close,
        // with no limit — and a leftover background process (an MSBuild/compiler
        // server, in real life) can hold the pipe open indefinitely. Found when
        // migrating TaskoMask: the CLI sat for 20+ minutes after the build finished.
        //
        // Here the "shell" exits immediately, but a child it started keeps running
        // for ~30s holding the inherited stdout. The limit (20s) sits well clear of both
        // outcomes: fixed, this returns after the 1s grace plus process start-up (which
        // took up to ~13s on a busy Windows CI runner); broken, it waits the child's
        // full ~30s. An earlier 20s child / 12s limit flaked on exactly that start-up.
        var original = ProcessRunner.OutputDrainGrace;
        ProcessRunner.OutputDrainGrace = TimeSpan.FromSeconds(1);
        try
        {
            var (file, args) = OperatingSystem.IsWindows()
                ? ("cmd", new[] { "/c", "start /b ping -n 31 127.0.0.1 & echo finished" })
                : ("sh", new[] { "-c", "sleep 30 & echo finished" });

            var clock = Stopwatch.StartNew();
            var result = await ProcessRunner.RunAsync(file, args, Path.GetTempPath());
            clock.Stop();

            Assert.Contains("finished", result.StandardOutput);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20),
                $"Waited {clock.Elapsed.TotalSeconds:0.#}s for a pipe held by a leftover child; it should give up after the grace period.");
        }
        finally
        {
            ProcessRunner.OutputDrainGrace = original;
        }
    }

    [Fact]
    public async Task CapturesOutputAndExitCode_OfAnOrdinaryProcess()
    {
        var (file, args) = OperatingSystem.IsWindows()
            ? ("cmd", new[] { "/c", "echo hello & exit 3" })
            : ("sh", new[] { "-c", "echo hello; exit 3" });

        var result = await ProcessRunner.RunAsync(file, args, Path.GetTempPath());

        Assert.Contains("hello", result.StandardOutput);
        Assert.Equal(3, result.ExitCode);
        Assert.False(result.Succeeded);
    }
}
