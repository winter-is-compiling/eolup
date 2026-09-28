namespace Rollforward.Core.Models;

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
    bool FailuresIdentified);
