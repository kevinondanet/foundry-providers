using InspectAzureAI.Examples.Runner;

// dotnet run --project examples -- <example> [flags] | list
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

return await ExampleRunner.MainAsync(args, cancellationToken: cts.Token);
