using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace WebApp.Tests;

// Passes on net8.0. After a bump to net10.0 it fails with HTTP 500 for as long as
// Microsoft.AspNetCore.Mvc.Testing stays on 8.x, and passes again once that is on 10.x:
// exactly the failure the opt-in package bump exists to fix.
public class ItemsTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Items_AreReturnedAsJson()
    {
        var items = await factory.CreateClient().GetFromJsonAsync<Item[]>("/items");

        Assert.Equal(["first", "second"], items!.Select(i => i.Name));
    }
}
