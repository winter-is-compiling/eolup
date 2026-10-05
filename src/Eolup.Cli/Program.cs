using System.CommandLine;
using Eolup.Cli;
using Eolup.Core;
using Eolup.Core.Config;
using Eolup.Core.Eol;
using Eolup.Core.Models;
using Eolup.Providers.DotNet;

var pathArgument = new Argument<string>("path")
{
    Description = "Path to the project to scan or remediate."
};

var scanCommand = new Command("scan", "Detect the current framework version and EOL status.")
{
    pathArgument
};
scanCommand.SetAction(async (parseResult, cancellationToken) =>
{
    // EolupEngine also normalizes this itself (defense-in-depth at the
    // library boundary), but resolving as early as possible here too keeps
    // every log/error message printed by the CLI unambiguous.
    var path = Path.GetFullPath(parseResult.GetValue(pathArgument)!);
    return await RunSafelyAsync(async () =>
    {
        var result = await CreateEngine().ScanAsync(path, cancellationToken);
        PrintScanResult(result);
        return 0;
    });
});

var failOnOption = new Option<string>("--fail-on")
{
    Description = "Exit with code 2 when the verdict is at least this bad: none (default), blocked, or needs-review.",
    DefaultValueFactory = _ => "none",
};
failOnOption.AcceptOnlyFromAmong(FailOnPolicyExtensions.AcceptedValues.ToArray());

var chainOption = new Option<bool>("--chain")
{
    Description = "Keep upgrading hop by hop (net6.0 -> net8.0 -> net10.0) while each hop is HighConfidence, " +
                  "stopping at the first that isn't. Same as `chain: true` in .eolup.yml. Default: one hop per run.",
};

var bumpPackagesOption = new Option<bool>("--bump-packages")
{
    Description = "If the framework bump alone breaks the build or tests, move the packages that version with the " +
                  "framework (ASP.NET Core, EF Core, Microsoft.Extensions.*) to the target major and re-run; kept only if " +
                  "that makes the migration pass. Same as `bumpPackages: true` in .eolup.yml. Default: off.",
};

var buildTimeoutOption = new Option<int?>("--build-timeout")
{
    Description = "Minutes a restore or build may run before Eolup stops it. Same as `buildTimeoutMinutes` in .eolup.yml. " +
                  "Default: 30. A build that is stopped can't be verified.",
};

var testTimeoutOption = new Option<int?>("--test-timeout")
{
    Description = "Minutes the test run may take before Eolup stops it (the verdict is then Blocked). " +
                  "Same as `testTimeoutMinutes` in .eolup.yml. Default: 60.",
};

var remediateCommand = new Command("remediate", "Attempt to migrate the project and report a confidence verdict.")
{
    pathArgument,
    failOnOption,
    chainOption,
    bumpPackagesOption,
    buildTimeoutOption,
    testTimeoutOption
};
remediateCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var path = Path.GetFullPath(parseResult.GetValue(pathArgument)!);
    var failOn = parseResult.GetValue(failOnOption);
    var chainFlag = parseResult.GetValue(chainOption);
    var bumpPackages = parseResult.GetValue(bumpPackagesOption);
    var buildTimeoutMinutes = parseResult.GetValue(buildTimeoutOption);
    var testTimeoutMinutes = parseResult.GetValue(testTimeoutOption);
    return await RunSafelyAsync(async () =>
    {
        var chain = chainFlag || EolupConfigLoader.Load(path).Chain;
        var engine = CreateEngine(
            bumpPackages, Minutes(buildTimeoutMinutes, "--build-timeout"), Minutes(testTimeoutMinutes, "--test-timeout"));
        var run = await engine.RemediateChainAsync(
            path, chain ? EolupEngine.MaxChainHops : 1, cancellationToken);
        PrintRemediationRun(run);

        // A PR only ever covers HighConfidence hops: with chaining, that's every hop
        // before the one that stopped the run (its branch stays local for inspection).
        var publishable = run.LastHighConfidence;
        if (publishable?.Result.BranchName is { } branch)
        {
            var steps = run.Hops.TakeWhile(h => h != publishable).Append(publishable).ToList();
            var prOutcome = await PullRequestPublisher.TryPublishAsync(
                path, branch,
                title: steps.Count == 1
                    ? $"Eolup: upgrade to {publishable.To}"
                    : $"Eolup: upgrade {steps[0].From} to {publishable.To} ({steps.Count} steps)",
                body: PullRequestBody(run, steps),
                cancellationToken);

            Console.WriteLine();
            Console.WriteLine(prOutcome);
        }

        // Exit code 2 (distinct from 1 = error) so CI can tell "the tool ran fine but
        // the verdict trips the team's policy" from "the tool itself failed". Judged on
        // the last hop attempted — with chaining, the one that stopped the run.
        var verdict = run.Final.Result.Verdict;
        var policy = FailOnPolicyExtensions.Parse(failOn);
        if (policy.ShouldFail(verdict))
        {
            Console.Error.WriteLine($"Failing: verdict {verdict} trips --fail-on {failOn}.");
            return 2;
        }

        return 0;
    });
});

var rootCommand = new RootCommand("Eolup — fleet-wide framework EOL scanning and confidence-scored remediation.")
{
    scanCommand,
    remediateCommand
};

return await rootCommand.Parse(args).InvokeAsync();

static EolupEngine CreateEngine(bool bumpPackages = false, TimeSpan? buildTimeout = null, TimeSpan? testTimeout = null) =>
    new(new EndOfLifeDateClient(), new DotNetLanguageProvider(
        new DotNetProviderOptions { BumpPackages = bumpPackages, BuildTimeout = buildTimeout, TestTimeout = testTimeout }));

// A timeout given on the command line, in minutes: unset means "use .eolup.yml, then the default".
static TimeSpan? Minutes(int? minutes, string flag) =>
    minutes switch
    {
        null => null,
        < 1 => throw new EolupUserException($"{flag} must be at least 1 (a number of minutes), but it is {minutes}."),
        int m => TimeSpan.FromMinutes(m),
    };

static async Task<int> RunSafelyAsync(Func<Task<int>> action)
{
    try
    {
        return await action();
    }
    catch (EolupUserException ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }
}

static void PrintScanResult(ScanResult result)
{
    Console.WriteLine($"Project:        {result.ProjectPath}");
    Console.WriteLine($"Current:        {result.CurrentVersion}");
    Console.WriteLine($"Target:         {result.TargetDisplay}");
    if (result.UpgradePath.Count > 1)
        Console.WriteLine($"Upgrade path:   {result.CurrentVersion} -> {string.Join(" -> ", result.UpgradePath)}  (one step per run)");
    Console.WriteLine($"Status:         {result.Status}");
    Console.WriteLine($"EOL date:       {result.EolDate?.ToString() ?? "unknown"}");
    Console.WriteLine($"Days until EOL: {result.DaysUntilEol?.ToString() ?? "n/a"}");
    foreach (var note in result.Notes)
        Console.WriteLine($"Note:           {note}");
}

static void PrintRemediationResult(RemediationResult result)
{
    Console.WriteLine($"Verdict: {result.Verdict}");
    Console.WriteLine($"Branch:  {result.BranchName ?? "n/a"}");
    Console.WriteLine("Reasons:");
    foreach (var reason in result.Reasons)
        Console.WriteLine($"  - {reason}");
}

static void PrintRemediationRun(ChainedRemediation run)
{
    if (run.Hops.Count == 1)
    {
        PrintRemediationResult(run.Final.Result);
        return;
    }

    for (var i = 0; i < run.Hops.Count; i++)
    {
        var hop = run.Hops[i];
        Console.WriteLine($"=== Step {i + 1}: {hop.From} -> {hop.To}");
        PrintRemediationResult(hop.Result);
        Console.WriteLine();
    }

    if (run.Final.Result.Verdict != ConfidenceVerdict.HighConfidence)
    {
        var done = run.LastHighConfidence;
        Console.WriteLine(done is null
            ? "Stopped at the first step."
            : $"Stopped at {run.Final.From} -> {run.Final.To} ({run.Final.Result.Verdict}); its branch " +
              $"'{run.Final.Result.BranchName}' is left locally for inspection. The PR covers the steps up to {done.To}.");
    }
}

static string PullRequestBody(ChainedRemediation run, IReadOnlyList<RemediationHop> steps)
{
    var body = new System.Text.StringBuilder("Opened automatically by Eolup.\n\n");

    if (steps.Count == 1)
    {
        body.Append("Why this is considered safe to review quickly:\n");
        foreach (var reason in steps[0].Result.Reasons)
            body.Append($"- {reason}\n");
    }
    else
    {
        body.Append($"Upgraded in {steps.Count} steps, one commit each, every step built and tested on its own:\n");
        foreach (var step in steps)
            body.Append($"- **{step.From} → {step.To}**: {string.Join(" ", step.Result.Reasons)}\n");
    }

    if (run.Final.Result.Verdict != ConfidenceVerdict.HighConfidence)
    {
        body.Append($"\nNot included: the next step, **{run.Final.From} → {run.Final.To}**, came out " +
                    $"{run.Final.Result.Verdict} and needs a human first:\n");
        foreach (var reason in run.Final.Result.Reasons)
            body.Append($"- {reason}\n");
    }

    return body.ToString();
}
