using Rollforward.Core.Models;
using Xunit;

namespace Rollforward.Core.Tests;

public class FailOnPolicyTests
{
    [Theory]
    [InlineData(null, FailOnPolicy.None)]
    [InlineData("", FailOnPolicy.None)]
    [InlineData("none", FailOnPolicy.None)]
    [InlineData("blocked", FailOnPolicy.Blocked)]
    [InlineData("needs-review", FailOnPolicy.NeedsReview)]
    [InlineData("Needs-Review", FailOnPolicy.NeedsReview)] // Action inputs are easy to capitalise
    public void Parse_AcceptsTheDocumentedValues(string? input, FailOnPolicy expected) =>
        Assert.Equal(expected, FailOnPolicyExtensions.Parse(input));

    [Fact]
    public void Parse_RejectsUnknownValues_ListingWhatIsAccepted()
    {
        var ex = Assert.Throws<RollforwardUserException>(() => FailOnPolicyExtensions.Parse("always"));
        Assert.Contains("needs-review", ex.Message);
    }

    [Theory]
    [InlineData(FailOnPolicy.None, ConfidenceVerdict.HighConfidence, false)]
    [InlineData(FailOnPolicy.None, ConfidenceVerdict.NeedsReview, false)]
    [InlineData(FailOnPolicy.None, ConfidenceVerdict.Blocked, false)]
    [InlineData(FailOnPolicy.Blocked, ConfidenceVerdict.HighConfidence, false)]
    [InlineData(FailOnPolicy.Blocked, ConfidenceVerdict.NeedsReview, false)]
    [InlineData(FailOnPolicy.Blocked, ConfidenceVerdict.Blocked, true)]
    [InlineData(FailOnPolicy.NeedsReview, ConfidenceVerdict.HighConfidence, false)]
    [InlineData(FailOnPolicy.NeedsReview, ConfidenceVerdict.NeedsReview, true)]
    [InlineData(FailOnPolicy.NeedsReview, ConfidenceVerdict.Blocked, true)]
    public void ShouldFail_MatchesThePolicyTable(FailOnPolicy policy, ConfidenceVerdict verdict, bool expected) =>
        Assert.Equal(expected, policy.ShouldFail(verdict));
}
