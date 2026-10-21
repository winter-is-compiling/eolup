namespace Eolup.Core.Models;

/// <summary>
/// How a checkout (a branch name, or a commit when HEAD is detached) is shown to the user, and the command that goes
/// back to it. Shared by the engine, the .NET provider and the CLI so they all say it the same way.
/// </summary>
public static class CheckoutRef
{
    /// <summary>A full commit hash (SHA-1 or SHA-256) rather than a branch name.</summary>
    public static bool IsCommit(string checkout) =>
        checkout.Length is 40 or 64 && checkout.All(Uri.IsHexDigit);

    /// <summary>"'main'" for a branch, "commit a1b2c3d" for a detached commit.</summary>
    public static string Describe(string checkout) =>
        IsCommit(checkout) ? $"commit {checkout[..7]}" : $"'{checkout}'";

    /// <summary>The command that goes back: <c>git switch main</c>, or <c>git switch --detach a1b2c3d…</c>.</summary>
    public static string WayBack(string checkout) =>
        IsCommit(checkout) ? $"git switch --detach {checkout}" : $"git switch {checkout}";
}
