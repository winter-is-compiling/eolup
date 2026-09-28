using Rollforward.Core.Infrastructure;

namespace Rollforward.Cli;

/// <summary>
/// Pushes a remediation branch and opens a PR via the `gh` CLI — chosen over
/// pulling in Octokit.NET because any developer running this already has `gh`
/// installed, so it's zero new dependency. See ARCHITECTURE.md.
///
/// This is deliberately best-effort: a missing remote, a missing/unauthenticated
/// `gh`, or a push failure should never crash `rollforward remediate` or hide the
/// confidence verdict that was already computed — they just mean no PR gets
/// opened, which is reported back, not thrown.
/// </summary>
internal static class PullRequestPublisher
{
    public static async Task<string> TryPublishAsync(
        string projectPath, string branchName, string title, string body, CancellationToken cancellationToken)
    {
        var remoteCheck = await ProcessRunner.RunAsync("git", "remote get-url origin", projectPath, cancellationToken);
        if (!remoteCheck.Succeeded)
            return $"No git remote 'origin' configured — branch '{branchName}' is ready locally, but no PR was opened.";

        var push = await ProcessRunner.RunAsync("git", $"push -u origin {branchName}", projectPath, cancellationToken);
        if (!push.Succeeded)
            return $"Could not push branch '{branchName}' to origin: {push.StandardError.Trim()}";

        var create = await ProcessRunner.RunAsync(
            "gh", ["pr", "create", "--title", title, "--body", body, "--head", branchName], projectPath, cancellationToken);

        if (!create.Succeeded)
            return $"Branch '{branchName}' was pushed, but `gh pr create` failed " +
                   $"(is the GitHub CLI installed and authenticated?): {create.StandardError.Trim()}";

        return create.StandardOutput.Trim(); // gh prints the PR URL on success
    }
}
