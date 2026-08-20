using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Runtime.Versioning;

namespace WordFlow.Infrastructure.Windows;

[SupportedOSPlatform("windows")]
public sealed class SingleInstanceCoordinator : IAsyncDisposable
{
    private const byte ActivateCommand = 1;
    private const byte Acknowledged = 2;
    private readonly CancellationTokenSource shutdown = new();
    private Thread? mutexThread;
    private Task? listenerTask;
    private int disposed;

    private SingleInstanceCoordinator(bool isPrimary, bool activationWasSignaled, Thread? mutexThread, Task? listenerTask)
    {
        IsPrimary = isPrimary;
        ActivationWasSignaled = activationWasSignaled;
        this.mutexThread = mutexThread;
        this.listenerTask = listenerTask;
    }

    public bool IsPrimary { get; }
    public bool ActivationWasSignaled { get; }

    public static Task<SingleInstanceCoordinator> StartAsync(
        string applicationId,
        Func<CancellationToken, Task> activationCallback,
        TimeSpan activationTimeout,
        CancellationToken cancellationToken) =>
        StartCoreAsync(applicationId, activationCallback, activationTimeout, cancellationToken, retryElection: true);

    private static async Task<SingleInstanceCoordinator> StartCoreAsync(
        string applicationId,
        Func<CancellationToken, Task> activationCallback,
        TimeSpan activationTimeout,
        CancellationToken cancellationToken,
        bool retryElection)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Single-instance coordination requires Windows.");
        if (string.IsNullOrWhiteSpace(applicationId)) throw new ArgumentException("An application ID is required.", nameof(applicationId));
        ArgumentNullException.ThrowIfNull(activationCallback);
        if (activationTimeout <= TimeSpan.Zero && activationTimeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(activationTimeout));
        cancellationToken.ThrowIfCancellationRequested();

        (string mutexName, string pipeName) = BuildNames(applicationId);
        var election = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new SingleInstanceCoordinator(isPrimary: true, activationWasSignaled: false, null, null);
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
            catch (TimeoutException) when (retryElection)
            {
                return await StartCoreAsync(
                    applicationId, activationCallback, activationTimeout, cancellationToken, retryElection: false)
                    .ConfigureAwait(false);
            }
            return new SingleInstanceCoordinator(isPrimary: false, activationWasSignaled: true, null, null);
        }

        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task listener = coordinator.ListenAsync(pipeName, activationCallback, ready);
        coordinator.listenerTask = listener;
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

    private async Task ListenAsync(string pipeName, Func<CancellationToken, Task> activationCallback, TaskCompletionSource ready)
    {
        bool announcedReady = false;
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                await using var pipe = new NamedPipeServerStream(
                    pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                if (!announcedReady)
                {
                    announcedReady = true;
                    ready.TrySetResult();
                }

                await pipe.WaitForConnectionAsync(shutdown.Token).ConfigureAwait(false);
                int command = await ReadByteAsync(pipe, shutdown.Token).ConfigureAwait(false);
                if (command != ActivateCommand) continue;
                try { await activationCallback(shutdown.Token).WaitAsync(shutdown.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
                catch { }
                if (shutdown.IsCancellationRequested) continue;
                try
                {
                    await pipe.WriteAsync(new[] { Acknowledged }, shutdown.Token).ConfigureAwait(false);
                    await pipe.FlushAsync(shutdown.Token).ConfigureAwait(false);
                }
                catch (IOException) { }
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ready.TrySetException(exception);
        }
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
            if (acknowledgement != Acknowledged) throw new IOException("The primary instance returned an invalid activation acknowledgement.");
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
            catch (OperationCanceledException) { }
        }
        if (mutexThread is not null && !mutexThread.Join(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("The single-instance mutex thread did not stop.");
        shutdown.Dispose();
    }
}
