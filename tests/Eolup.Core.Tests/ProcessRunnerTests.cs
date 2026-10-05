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
    public async Task AProcessThatOutlivesItsLimit_IsKilled_AndTheErrorSaysWhatRanAndWhatItHadWritten()
    {
        // Found by validating v0.3.0 on real repos: one fixed 5-minute limit for every child process killed
        // legitimate restores, builds and test suites, and the error blamed "a lingering background process"
        // without naming the command, the limit or what the process had been doing.
        var (file, args) = OperatingSystem.IsWindows()
            ? ("cmd", new[] { "/c", "echo started & ping -n 31 127.0.0.1 > nul" })
            : ("sh", new[] { "-c", "echo started; sleep 30" });

        var clock = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<ProcessTimeoutException>(
            () => ProcessRunner.RunAsync(file, args, Path.GetTempPath(), TimeSpan.FromSeconds(2)));
        clock.Stop();

        Assert.Equal(file, error.Command);
        Assert.Equal(TimeSpan.FromSeconds(2), error.Timeout);
        Assert.Contains("started", error.PartialOutput);
        Assert.Contains("did not finish within 2 seconds", error.Message);
        Assert.DoesNotContain("lingering", error.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(25),
            $"Took {clock.Elapsed.TotalSeconds:0.#}s: the process should be killed at its limit, not run to its natural end.");
    }

    [Fact]
    public async Task ATimeoutIsAUserError_SoAnUnhandledOneStillReachesTheCliAsACleanMessage()
    {
        var (file, args) = OperatingSystem.IsWindows()
            ? ("cmd", new[] { "/c", "ping -n 31 127.0.0.1 > nul" })
            : ("sh", new[] { "-c", "sleep 30" });

        await Assert.ThrowsAsync<ProcessTimeoutException>(
            () => ProcessRunner.RunAsync(file, args, Path.GetTempPath(), TimeSpan.FromSeconds(1)));

        Assert.True(typeof(EolupUserException).IsAssignableFrom(typeof(ProcessTimeoutException)));
    }

    [Fact]
    public async Task AProcessThatTakesLongerThanTheOldFixedLimitButFinishesInsideItsOwn_IsUnaffected()
    {
        // The limit is per call now: a healthy run that is merely slow completes as long as it fits its own limit.
        var (file, args) = OperatingSystem.IsWindows()
            ? ("cmd", new[] { "/c", "echo done & ping -n 4 127.0.0.1 > nul" })
            : ("sh", new[] { "-c", "echo done; sleep 3" });

        var result = await ProcessRunner.RunAsync(file, args, Path.GetTempPath(), TimeSpan.FromMinutes(10));

        Assert.True(result.Succeeded);
        Assert.Contains("done", result.StandardOutput);
    }

    [Theory]
    [InlineData("dotnet", "test", "dotnet test")]
    [InlineData("git", "rev-parse", "git rev-parse")]
    [InlineData("C:\\Program Files\\dotnet\\dotnet.exe", "build", "dotnet build")]
    [InlineData("sh", "-c", "sh")]
    [InlineData("cmd", "/c", "cmd")]
    [InlineData("dotnet", "--version", "dotnet")]
    [InlineData("dotnet", null, "dotnet")]
    [InlineData("dotnet", "C:\\repo\\App.sln", "dotnet")] // a path is not a subcommand and must not be echoed into a message
    public void ACommandIsNamedByItsProgramAndSubcommand(string fileName, string? firstArgument, string expected) =>
        Assert.Equal(expected, ProcessRunner.Describe(fileName, firstArgument));

    [Fact]
    public void ALimitIsDescribedInWords()
    {
        Assert.Equal("30 minutes", ProcessTimeoutException.Describe(TimeSpan.FromMinutes(30)));
        Assert.Equal("1 minute", ProcessTimeoutException.Describe(TimeSpan.FromMinutes(1)));
        Assert.Equal("90 seconds", ProcessTimeoutException.Describe(TimeSpan.FromSeconds(90)));
        Assert.Equal("45 seconds", ProcessTimeoutException.Describe(TimeSpan.FromSeconds(45)));
        Assert.Equal("1 second", ProcessTimeoutException.Describe(TimeSpan.FromMilliseconds(400)));
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
