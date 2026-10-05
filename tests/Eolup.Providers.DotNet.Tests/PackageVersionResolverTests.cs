using System.Net;
using System.Text;
using Xunit;

namespace Eolup.Providers.DotNet.Tests;

public class PackageVersionResolverTests
{
    private sealed class FakeSource(params string[] versions) : IPackageVersionSource
    {
        public Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(versions);
    }

    private sealed class FailingSource(Exception error) : IPackageVersionSource
    {
        public Task<IReadOnlyList<string>> GetVersionsAsync(string packageId, CancellationToken cancellationToken = default) =>
            throw error;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static Task<string?> Resolve(IPackageVersionSource source, int major = 10) =>
        PackageVersionResolver.LatestStableOnMajorAsync(source, "Some.Package", major);

    [Fact]
    public async Task PicksTheNewestStableVersionOnTheTargetMajor()
    {
        var picked = await Resolve(new FakeSource("8.0.11", "10.0.0", "10.0.2", "10.0.10", "10.0.9", "11.0.0"));

        Assert.Equal("10.0.10", picked); // numeric order, not text order
    }

    [Fact]
    public async Task IgnoresPrereleases()
    {
        Assert.Equal("10.0.1", await Resolve(new FakeSource("10.0.1", "10.0.2-rc.1", "10.1.0-preview.3")));
    }

    [Fact]
    public async Task OnlyPrereleasesOnThatMajor_MeansNothingToBumpTo()
    {
        Assert.Null(await Resolve(new FakeSource("8.0.11", "10.0.0-rc.1")));
    }

    [Fact]
    public async Task NoVersionOnThatMajor_ReturnsNull()
    {
        Assert.Null(await Resolve(new FakeSource("8.0.11", "9.0.4")));
    }

    [Fact]
    public async Task ToleratesBuildMetadataFourPartVersionsAndJunk()
    {
        Assert.Equal("10.0.3.1", await Resolve(new FakeSource("10.0.3+abc", "10.0.3.1", "not-a-version", "", "10")));
    }

    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(TaskCanceledException))]
    [InlineData(typeof(System.Text.Json.JsonException))]
    public async Task ALookupThatFailsIsNullNotAnException(Type exceptionType)
    {
        var error = (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.Null(await Resolve(new FailingSource(error)));
    }

    [Fact]
    public async Task ACancelledRunStillCancels()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            PackageVersionResolver.LatestStableOnMajorAsync(new FailingSource(new TaskCanceledException()), "x", 10, cts.Token));
    }

    [Fact]
    public async Task NuGetOrgSource_ReadsTheFlatContainerIndex_ByLowerCasedId()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"versions\":[\"8.0.11\",\"10.0.1\",\"10.0.2\"]}", Encoding.UTF8, "application/json"),
        });
        var source = new NuGetOrgVersionSource(new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/v3-flatcontainer/") });

        var versions = await source.GetVersionsAsync("Microsoft.AspNetCore.Mvc.Testing");

        Assert.Equal(["8.0.11", "10.0.1", "10.0.2"], versions);
        Assert.Equal("/v3-flatcontainer/microsoft.aspnetcore.mvc.testing/index.json", handler.Requests.Single().AbsolutePath);
    }

    [Fact]
    public async Task NuGetOrgSource_UnknownPackageIsEmpty_AnythingElseIsAnError()
    {
        var notFound = new NuGetOrgVersionSource(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)))
            { BaseAddress = new Uri("https://api.example.test/") });
        var broken = new NuGetOrgVersionSource(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)))
            { BaseAddress = new Uri("https://api.example.test/") });

        Assert.Empty(await notFound.GetVersionsAsync("nope"));
        await Assert.ThrowsAsync<HttpRequestException>(() => broken.GetVersionsAsync("x"));
        // ...and the resolver turns that error into "couldn't resolve".
        Assert.Null(await PackageVersionResolver.LatestStableOnMajorAsync(broken, "x", 10));
    }
}
