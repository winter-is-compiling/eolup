namespace SampleApp.Tests;

// A test project that exists, builds and passes — but never calls into SampleApp.
// Eolup must not treat that as verification of the migration.
public class PlaceholderTests
{
    [Fact]
    public void Passes_WithoutExercisingAnyApplicationCode()
    {
        Assert.True(true);
    }
}
