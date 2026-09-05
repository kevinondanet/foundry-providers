using InspectAzureAI.SweShowcase;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // Let the runner stop its samples, remove the sandboxes and write a `cancelled` log before the process exits.
    e.Cancel = true;
    shutdown.Cancel();
};

return await Cli.RunAsync(args, shutdown.Token);
