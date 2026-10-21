using Eolup.Providers.DotNet;
using Xunit;

namespace Eolup.Providers.DotNet.Tests;

public class TestConsoleOutputTests
{
    // The shape `dotnet test <sln>` prints when one test host crashes and another passes.
    private const string OneCrashed = """
        Test run for C:\repo\src\GoodTests\bin\Debug\net8.0\GoodTests.dll (.NETCoreApp,Version=v8.0)
        Test run for C:\repo\src\CrashTests\bin\Debug\net8.0\CrashTests.dll (.NETCoreApp,Version=v8.0)
        A total of 1 test files matched the specified pattern.
        The active test run was aborted. Reason: Test host process crashed

        Test Run Aborted.
        Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 326 ms - GoodTests.dll (net8.0)
        """;

    [Fact]
    public void AnAssemblyThatStartedButNeverSummarised_DidNotReport()
    {
        Assert.Equal(["CrashTests"], TestConsoleOutput.AssembliesThatDidNotReport(OneCrashed));
    }

    [Fact]
    public void WhenEveryAssemblySummarised_NoneIsReported()
    {
        var output = """
            Test run for /repo/A/bin/Debug/net8.0/A.dll (.NETCoreApp,Version=v8.0)
            Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 1 s - A.dll (net8.0)
            """;
        Assert.Empty(TestConsoleOutput.AssembliesThatDidNotReport(output));
    }

    [Fact]
    public void APathWithSpaces_IsStillOneAssembly()
    {
        var output = @"Test run for C:\My Repo\Tests\bin\Debug\net8.0\My.Tests.dll (.NETCoreApp,Version=v8.0)";
        Assert.Equal(["My.Tests"], TestConsoleOutput.AssembliesThatDidNotReport(output));
    }

    [Fact]
    public void OutputThatNamesNothing_GivesNoNames()
    {
        Assert.Empty(TestConsoleOutput.AssembliesThatDidNotReport("Build succeeded."));
        Assert.Empty(TestConsoleOutput.AssembliesThatDidNotReport(""));
    }

    [Fact]
    public void TheCrashLineIsFound_MostSpecificFirst()
    {
        Assert.Equal("The active test run was aborted. Reason: Test host process crashed", TestConsoleOutput.CrashText(OneCrashed));
    }

    [Fact]
    public void ASuiteThatSimplyFailedHasNoCrashText()
    {
        Assert.Null(TestConsoleOutput.CrashText("Failed!  - Failed:     1, Passed:     0 - A.dll (net8.0)"));
    }
}
