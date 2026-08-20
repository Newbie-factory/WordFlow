using WordFlow.Infrastructure.Windows;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: WordFlow.SingleInstance.TestHost <application-id> <state-directory>");
    return 2;
}

string applicationId = args[0];
string stateDirectory = Path.GetFullPath(args[1]);
string eventsPath = Path.Combine(stateDirectory, "events.log");
string stopPath = Path.Combine(stateDirectory, "stop");
Directory.CreateDirectory(stateDirectory);

try
{
    await using SingleInstanceCoordinator coordinator = await SingleInstanceCoordinator.StartAsync(
        applicationId,
        _ => AppendAsync($"activation pid={Environment.ProcessId}"),
        TimeSpan.FromSeconds(5),
        CancellationToken.None);
    if (!coordinator.IsPrimary)
    {
        await AppendAsync($"secondary pid={Environment.ProcessId} signaled={coordinator.ActivationWasSignaled}");
        return coordinator.ActivationWasSignaled ? 0 : 3;
    }

    await AppendAsync($"bootstrap pid={Environment.ProcessId}");
    await AppendAsync($"ready pid={Environment.ProcessId}");
    while (!File.Exists(stopPath)) await Task.Delay(50);
    await AppendAsync($"stopping pid={Environment.ProcessId}");
    return 0;
}
catch (Exception exception)
{
    await AppendAsync($"failure pid={Environment.ProcessId} type={exception.GetType().Name}");
    return 1;
}

Task AppendAsync(string line) => File.AppendAllTextAsync(eventsPath, line + Environment.NewLine);
