namespace Eolup.Core.Models;

/// <summary>
/// What the test suite did on the untouched code, compared with the migrated code.
/// Only gathered when the tests fail after a migration: a failure alone can't say
/// whether the migration caused it or the suite was already broken (a test needing a
/// database, a runtime that isn't installed), and blaming the migration for the
/// latter makes the verdict useless.
/// </summary>
/// <param name="BaselinePassed">Whether the whole suite passed before the migration.</param>
/// <param name="NewFailures">Tests that fail after the migration but didn't before.</param>
/// <param name="AlreadyFailing">Tests that fail after the migration and already failed before.</param>
/// <param name="FailuresIdentified">
/// Whether individual test results were available on both sides. False when the
/// suite couldn't run far enough to report per-test outcomes (e.g. the test host
/// failed to start), in which case the two lists are empty and mean nothing.
/// </param>
public sealed record TestComparison(
    bool BaselinePassed,
    IReadOnlyList<string> NewFailures,
    IReadOnlyList<string> AlreadyFailing,
    bool FailuresIdentified)
{
    /// <summary>
    /// Whether a failing suite should be blamed on the migration: no comparison could be made (so assume
    /// it did), the suite passed before it, or specific tests newly fail. False when everything that fails
    /// was already failing, or the untouched code gave nothing to compare against. The one rule behind
    /// "name the stale packages" and "retry with a package bump" — both only make sense when the migration
    /// is the likely cause.
    /// </summary>
    public static bool BlamesMigration(TestComparison? comparison) =>
        comparison is null
        || comparison.BaselinePassed
        || (comparison.FailuresIdentified && comparison.NewFailures.Count > 0);
}
