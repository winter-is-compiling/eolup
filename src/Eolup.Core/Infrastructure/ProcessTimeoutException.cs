using System.Globalization;

namespace Eolup.Core.Infrastructure;

/// <summary>
/// A child process ran past its time limit and was killed. It is an <see cref="EolupUserException"/>, so one that
/// nobody handles still reaches the CLI as a clean message; callers that can turn it into something better
/// (a verdict that says "the test run did not finish", with the setting that raises the limit) catch it by type.
/// </summary>
public sealed class ProcessTimeoutException(string command, TimeSpan timeout, string partialOutput)
    : EolupUserException($"'{command}' did not finish within {Describe(timeout)} and was stopped.")
{
    /// <summary>What was run, as a person would say it: the program and its subcommand, e.g. "dotnet test".</summary>
    public string Command { get; } = command;

    /// <summary>The limit that was exceeded.</summary>
    public TimeSpan Timeout { get; } = timeout;

    /// <summary>Whatever the process had written before it was stopped.</summary>
    public string PartialOutput { get; } = partialOutput;

    /// <summary>A limit in words: "30 minutes", "1 minute", "45 seconds".</summary>
    public static string Describe(TimeSpan limit)
    {
        var minutes = limit.TotalMinutes;
        if (minutes >= 1 && minutes == Math.Floor(minutes))
            return minutes == 1 ? "1 minute" : $"{minutes.ToString("0", CultureInfo.InvariantCulture)} minutes";

        var seconds = Math.Max(1, (int)Math.Round(limit.TotalSeconds));
        return seconds == 1 ? "1 second" : $"{seconds} seconds";
    }
}
