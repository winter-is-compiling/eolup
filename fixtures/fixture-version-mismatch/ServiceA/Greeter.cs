namespace ServiceA;

// Two independently-versioned services under one shared parent directory —
// mirrors a real monorepo shape (found via dotnet/eShop, though that repo's
// services happened to already agree on version). No tests, so the verdict is
// Blocked; what this fixture exercises is which projects one run bumps: only
// the oldest (ServiceA, net8.0) — ServiceB is already on net10.0 and is left alone.
public class Greeter
{
    public string Greet(string name) => $"Hello from ServiceA, {name}!";
}
