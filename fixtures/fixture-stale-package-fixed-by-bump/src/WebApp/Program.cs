var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/items", () => Results.Ok(new[] { new Item(1, "first"), new Item(2, "second") }));

app.Run();

public sealed record Item(int Id, string Name);

// Lets the test project host the app in memory.
public partial class Program { }
