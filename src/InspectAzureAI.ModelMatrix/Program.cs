using InspectAzureAI.ModelMatrix;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

return await MatrixCli.RunAsync(args, Console.Out, shutdown.Token);
