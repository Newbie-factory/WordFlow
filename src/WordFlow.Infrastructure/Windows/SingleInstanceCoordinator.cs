using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace WordFlow.Infrastructure.Windows;

public sealed class ActivationFailedException : Exception
{
    public ActivationFailedException(string message) : base(message) { }
}

[SupportedOSPlatform("windows")]
public sealed class SingleInstanceCoordinator : IAsyncDisposable
{
    private const byte ActivateCommand = 1;
    private const byte ActivationSucceeded = 2;
    private const byte ActivationFailed = 3;
    private readonly CancellationTokenSource shutdown = new();
    private Thread? mutexThread;
    private Task? listenerTask;
    private int disposed;

    private SingleInstanceCoordinator(bool isPrimary, bool activationWasSignaled)
    {
        IsPrimary = isPrimary;
        ActivationWasSignaled = activationWasSignaled;
    }

    public bool IsPrimary { get; }
    public bool ActivationWasSignaled { get; }
    public Task Completion => listenerTask ?? Task.CompletedTask;

    public static Task<SingleInstanceCoordinator> StartAsync(
        string applicationId,
        Func<CancellationToken, Task> activationCallback,
        TimeSpan activationTimeout,
        CancellationToken cancellationToken) =>
        StartAsync(applicationId, activationCallback, activationTimeout, cancellationToken,
            CreatePipeServer, backgroundFaultObserver: null);

    public static Task<SingleInstanceCoordinator> StartAsync(
        string applicationId,
        Func<CancellationToken, Task> activationCallback,
        TimeSpan activationTimeout,
        CancellationToken cancellationToken,
        Action<Exception> backgroundFaultObserver) =>
        StartAsync(applicationId, activationCallback, activationTimeout, cancellationToken,
            CreatePipeServer, backgroundFaultObserver);

    internal static Task<SingleInstanceCoordinator> StartAsync(
        string applicationId,
        Func<CancellationToken, Task> activationCallback,
        TimeSpan activationTimeout,
        CancellationToken cancellationToken,
        Func<string, NamedPipeServerStream> pipeServerFactory,
        Action<Exception>? backgroundFaultObserver) =>
        StartCoreAsync(applicationId, activationCallback, activationTimeout, cancellationToken,
            pipeServerFactory, backgroundFaultObserver, retryElection: true);

    private static async Task<SingleInstanceCoordinator> StartCoreAsync(
        string applicationId,
        Func<CancellationToken, Task> activationCallback,
        TimeSpan activationTimeout,
        CancellationToken cancellationToken,
        Func<string, NamedPipeServerStream> pipeServerFactory,
        Action<Exception>? backgroundFaultObserver,
        bool retryElection)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Single-instance coordination requires Windows.");
        if (string.IsNullOrWhiteSpace(applicationId)) throw new ArgumentException("An application ID is required.", nameof(applicationId));
        ArgumentNullException.ThrowIfNull(activationCallback);
        ArgumentNullException.ThrowIfNull(pipeServerFactory);
        if (activationTimeout <= TimeSpan.Zero && activationTimeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(activationTimeout));
        cancellationToken.ThrowIfCancellationRequested();

        (string mutexName, string pipeName) = BuildNames(applicationId);
        var election = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new SingleInstanceCoordinator(isPrimary: true, activationWasSignaled: false);
        var mutexThread = new Thread(() => coordinator.HoldMutex(mutexName, election))
        {
            IsBackground = true,
            Name = $"{applicationId} single-instance mutex",
        };
        coordinator.mutexThread = mutexThread;
        mutexThread.Start();
        bool isPrimary;
        try
        {
            isPrimary = await election.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await coordinator.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        if (!isPrimary)
        {
            await coordinator.DisposeAsync().ConfigureAwait(false);
            try
            {
                await SignalPrimaryAsync(pipeName, activationTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (retryElection && exception is TimeoutException or IOException)
            {
                return await StartCoreAsync(applicationId, activationCallback, activationTimeout, cancellationToken,
                    pipeServerFactory, backgroundFaultObserver, retryElection: false).ConfigureAwait(false);
            }
            return new SingleInstanceCoordinator(isPrimary: false, activationWasSignaled: true);
        }

        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.listenerTask = coordinator.ListenAsync(
            pipeName, activationCallback, pipeServerFactory, backgroundFaultObserver, ready);
        try
        {
            await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return coordinator;
        }
        catch
        {
            await coordinator.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void HoldMutex(string mutexName, TaskCompletionSource<bool> election)
    {
        try
        {
            using var mutex = new Mutex(initiallyOwned: false, mutexName);
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            election.TrySetResult(acquired);
            if (!acquired) return;
            shutdown.Token.WaitHandle.WaitOne();
            mutex.ReleaseMutex();
        }
        catch (Exception exception)
        {
            election.TrySetException(exception);
        }
    }

    private async Task ListenAsync(
        string pipeName,
        Func<CancellationToken, Task> activationCallback,
        Func<string, NamedPipeServerStream> pipeServerFactory,
        Action<Exception>? backgroundFaultObserver,
        TaskCompletionSource ready)
    {
        bool announcedReady = false;
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                await using NamedPipeServerStream pipe = pipeServerFactory(pipeName);
                if (!announcedReady)
                {
                    announcedReady = true;
                    ready.TrySetResult();
                }
                try
                {
                    await pipe.WaitForConnectionAsync(shutdown.Token).ConfigureAwait(false);
                    int command = await ReadByteAsync(pipe, shutdown.Token).ConfigureAwait(false);
                    if (command != ActivateCommand) continue;

                    Task callbackTask;
                    try { callbackTask = activationCallback(shutdown.Token); }
                    catch (Exception exception)
                    {
                        ReportBackgroundFault(backgroundFaultObserver, exception);
                        await TryWriteResponseAsync(pipe, ActivationFailed, shutdown.Token).ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        await callbackTask.WaitAsync(shutdown.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                    {
                        ObserveDetached(callbackTask, backgroundFaultObserver);
                        continue;
                    }
                    catch (Exception exception)
                    {
                        ReportBackgroundFault(backgroundFaultObserver, exception);
                        await TryWriteResponseAsync(pipe, ActivationFailed, shutdown.Token).ConfigureAwait(false);
                        continue;
                    }

                    if (!shutdown.IsCancellationRequested)
                        await TryWriteResponseAsync(pipe, ActivationSucceeded, shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
                catch (IOException exception) when (!shutdown.IsCancellationRequested)
                {
                    ReportBackgroundFault(backgroundFaultObserver, exception);
                }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ready.TrySetException(exception);
            ReportBackgroundFault(backgroundFaultObserver, exception);
            shutdown.Cancel();
            throw;
        }
    }

    private static async Task TryWriteResponseAsync(Stream pipe, byte response, CancellationToken cancellationToken)
    {
        try
        {
            await pipe.WriteAsync(new[] { response }, cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) { }
    }

    private static void ObserveDetached(Task callbackTask, Action<Exception>? backgroundFaultObserver)
    {
        _ = callbackTask.ContinueWith(
            task => ReportBackgroundFault(backgroundFaultObserver,
                (Exception?)task.Exception?.Flatten() ?? new InvalidOperationException("A detached activation callback faulted.")),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void ReportBackgroundFault(Action<Exception>? observer, Exception exception)
    {
        try { observer?.Invoke(exception); }
        catch { }
    }

    private static async Task SignalPrimaryAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = timeout == Timeout.InfiniteTimeSpan ? null : new CancellationTokenSource(timeout);
        using var linked = timeoutSource is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);
            await pipe.WriteAsync(new[] { ActivateCommand }, linked.Token).ConfigureAwait(false);
            await pipe.FlushAsync(linked.Token).ConfigureAwait(false);
            int acknowledgement = await ReadByteAsync(pipe, linked.Token).ConfigureAwait(false);
            if (acknowledgement == ActivationFailed)
                throw new ActivationFailedException("The primary WordFlow instance could not activate its window.");
            if (acknowledgement != ActivationSucceeded)
                throw new IOException("The primary instance returned an invalid activation acknowledgement.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutSource?.IsCancellationRequested == true)
        {
            throw new TimeoutException("Timed out while signaling the primary WordFlow instance.");
        }
    }

    private static async Task<int> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1];
        int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        return read == 0 ? -1 : buffer[0];
    }

    private static NamedPipeServerStream CreatePipeServer(string pipeName) => new(
        pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static (string MutexName, string PipeName) BuildNames(string applicationId)
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current Windows user has no security identifier.");
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{applicationId}\0{sid}")))[..32];
        return ($"Local\\{applicationId}.{identity}", $"{applicationId}.{identity}");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        shutdown.Cancel();
        if (listenerTask is not null)
        {
            try { await listenerTask.ConfigureAwait(false); }
            catch { }
        }
        if (mutexThread is not null && !mutexThread.Join(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("The single-instance mutex thread did not stop.");
        shutdown.Dispose();
    }
}
