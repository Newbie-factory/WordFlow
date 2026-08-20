using WordFlow.Infrastructure.Windows;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace WordFlow.Infrastructure.Tests.Windows;

[SupportedOSPlatform("windows")]
public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public async Task Concurrent_startup_elects_exactly_one_primary()
    {
        string applicationId = UniqueApplicationId();
        SingleInstanceCoordinator[] coordinators = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => SingleInstanceCoordinator.StartAsync(
                applicationId, _ => Task.CompletedTask, TimeSpan.FromSeconds(5), CancellationToken.None)));

        try
        {
            Assert.Single(coordinators, coordinator => coordinator.IsPrimary);
            Assert.All(coordinators.Where(coordinator => !coordinator.IsPrimary),
                coordinator => Assert.True(coordinator.ActivationWasSignaled));
        }
        finally
        {
            await DisposeAllAsync(coordinators);
        }
    }

    [Fact]
    public async Task Secondary_launch_invokes_the_primary_activation_callback()
    {
        string applicationId = UniqueApplicationId();
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var primary = await SingleInstanceCoordinator.StartAsync(
            applicationId, _ => { activated.TrySetResult(); return Task.CompletedTask; },
            TimeSpan.FromSeconds(5), CancellationToken.None);

        await using var secondary = await SingleInstanceCoordinator.StartAsync(
            applicationId, _ => Task.CompletedTask, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(primary.IsPrimary);
        Assert.False(secondary.IsPrimary);
        Assert.True(secondary.ActivationWasSignaled);
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Secondary_times_out_when_primary_callback_does_not_complete()
    {
        string applicationId = UniqueApplicationId();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var primary = await SingleInstanceCoordinator.StartAsync(
            applicationId,
            async ct =>
            {
                callbackEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            },
            TimeSpan.FromSeconds(5), CancellationToken.None);

        await Assert.ThrowsAsync<TimeoutException>(() => SingleInstanceCoordinator.StartAsync(
            applicationId, _ => Task.CompletedTask, TimeSpan.FromMilliseconds(150), CancellationToken.None));
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Secondary_honors_cancellation_while_waiting_for_activation_acknowledgement()
    {
        string applicationId = UniqueApplicationId();
        await using var primary = await SingleInstanceCoordinator.StartAsync(
            applicationId,
            ct => Task.Delay(Timeout.InfiniteTimeSpan, ct),
            TimeSpan.FromSeconds(5), CancellationToken.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SingleInstanceCoordinator.StartAsync(
            applicationId, _ => Task.CompletedTask, TimeSpan.FromSeconds(5), cancellation.Token));
    }

    [Fact]
    public async Task Cancellation_racing_with_election_never_leaks_primary_ownership()
    {
        for (int attempt = 0; attempt < 32; attempt++)
        {
            string applicationId = UniqueApplicationId();
            using var cancellation = new CancellationTokenSource();
            Task<SingleInstanceCoordinator> startup = SingleInstanceCoordinator.StartAsync(
                applicationId, _ => Task.CompletedTask, TimeSpan.FromMilliseconds(250), cancellation.Token);
            cancellation.Cancel();

            try
            {
                SingleInstanceCoordinator coordinator = await startup;
                await coordinator.DisposeAsync();
            }
            catch (OperationCanceledException) { }

            await using var replacement = await SingleInstanceCoordinator.StartAsync(
                applicationId, _ => Task.CompletedTask, TimeSpan.FromMilliseconds(250), CancellationToken.None);
            Assert.True(replacement.IsPrimary);
        }
    }

    [Fact]
    public async Task Disposed_primary_releases_election_and_stops_its_listener()
    {
        string applicationId = UniqueApplicationId();
        var first = await SingleInstanceCoordinator.StartAsync(
            applicationId, _ => Task.CompletedTask, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(first.IsPrimary);

        await first.DisposeAsync();

        await using var replacement = await SingleInstanceCoordinator.StartAsync(
            applicationId, _ => Task.CompletedTask, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.True(replacement.IsPrimary);
    }

    [Fact]
    public async Task Secondary_re_elects_when_the_owner_disappears_before_pipe_activation()
    {
        string applicationId = UniqueApplicationId();
        string mutexName = UserScopedMutexName(applicationId);
        using var ownerReady = new ManualResetEventSlim();
        using var releaseOwner = new ManualResetEventSlim();
        var departingOwner = new Thread(() =>
        {
            using var mutex = new Mutex(initiallyOwned: false, mutexName);
            mutex.WaitOne();
            ownerReady.Set();
            releaseOwner.Wait();
            mutex.ReleaseMutex();
        });
        departingOwner.Start();
        Assert.True(ownerReady.Wait(TimeSpan.FromSeconds(5)));

        Task<SingleInstanceCoordinator> startup = SingleInstanceCoordinator.StartAsync(
            applicationId, _ => Task.CompletedTask, TimeSpan.FromMilliseconds(150), CancellationToken.None);
        releaseOwner.Set();
        Assert.True(departingOwner.Join(TimeSpan.FromSeconds(5)));

        await using var replacement = await startup;
        Assert.True(replacement.IsPrimary);
    }

    [Fact]
    public async Task Abandoned_user_scoped_mutex_is_recovered_as_the_new_primary()
    {
        string applicationId = UniqueApplicationId();
        string mutexName = UserScopedMutexName(applicationId);
        using var mutexAcquired = new ManualResetEventSlim();
        Mutex? abandonedMutex = null;
        var abandoningThread = new Thread(() =>
        {
            abandonedMutex = new Mutex(initiallyOwned: false, mutexName);
            abandonedMutex.WaitOne();
            mutexAcquired.Set();
        });
        abandoningThread.Start();
        Assert.True(mutexAcquired.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(abandoningThread.Join(TimeSpan.FromSeconds(5)));

        try
        {
            await using var replacement = await SingleInstanceCoordinator.StartAsync(
                applicationId, _ => Task.CompletedTask, TimeSpan.FromMilliseconds(250), CancellationToken.None);

            Assert.True(replacement.IsPrimary);
        }
        finally
        {
            abandonedMutex?.Dispose();
        }
    }

    [Fact]
    public async Task Invalid_activation_callback_does_not_kill_the_listener()
    {
        string applicationId = UniqueApplicationId();
        int attempts = 0;
        var secondAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var primary = await SingleInstanceCoordinator.StartAsync(
            applicationId,
            _ =>
            {
                if (Interlocked.Increment(ref attempts) == 1) throw new InvalidOperationException("boom");
                secondAttempt.TrySetResult();
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(5), CancellationToken.None);

        await using var firstSecondary = await SingleInstanceCoordinator.StartAsync(
            applicationId, _ => Task.CompletedTask, TimeSpan.FromSeconds(5), CancellationToken.None);
        await using var secondSecondary = await SingleInstanceCoordinator.StartAsync(
            applicationId, _ => Task.CompletedTask, TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(firstSecondary.ActivationWasSignaled);
        Assert.True(secondSecondary.ActivationWasSignaled);
        await secondAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, Volatile.Read(ref attempts));
    }

    [Fact]
    public async Task Disposal_does_not_wait_for_a_callback_that_ignores_cancellation()
    {
        string applicationId = UniqueApplicationId();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = await SingleInstanceCoordinator.StartAsync(
            applicationId,
            _ => { callbackEntered.TrySetResult(); return neverCompletes.Task; },
            TimeSpan.FromSeconds(5), CancellationToken.None);
        Task<SingleInstanceCoordinator> secondary = SingleInstanceCoordinator.StartAsync(
            applicationId, _ => Task.CompletedTask, TimeSpan.FromSeconds(5), CancellationToken.None);
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await primary.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<Exception>(() => secondary);
    }

    private static string UniqueApplicationId() => $"WordFlow.Tests.{Guid.NewGuid():N}";

    private static string UserScopedMutexName(string applicationId)
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The test user has no Windows SID.");
        string identity = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{applicationId}\0{sid}")))[..32];
        return $"Local\\{applicationId}.{identity}";
    }

    private static async Task DisposeAllAsync(IEnumerable<SingleInstanceCoordinator> coordinators)
    {
        foreach (SingleInstanceCoordinator coordinator in coordinators)
            await coordinator.DisposeAsync();
    }
}
