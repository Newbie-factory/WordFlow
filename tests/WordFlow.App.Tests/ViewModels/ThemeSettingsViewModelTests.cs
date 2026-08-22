using WordFlow.App.Bootstrap;
using WordFlow.App.Styling;
using WordFlow.App.ViewModels;
using WordFlow.Infrastructure.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WordFlow.App.Tests.ViewModels;

public sealed class ThemeSettingsViewModelTests
{
    [Fact]
    public async Task One_hundred_rapid_changes_preview_synchronously_and_persist_latest_once()
    {
        string root = Directory.CreateTempSubdirectory("wordflow-theme-vm-").FullName;
        try
        {
            var service = await CreateServiceAsync(root);
            int saves = 0;
            int decodes = 0;
            ImageTheme? saved = null;
            var cache = new ThemeImageCache(_ =>
            {
                decodes++;
                var bitmap = new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgra32, null);
                bitmap.Freeze();
                return bitmap;
            });
            string imagePath = Path.Combine(root, "skin.png");
            var viewModel = new ThemeSettingsViewModel(service, (theme, _) =>
            {
                Interlocked.Increment(ref saves);
                saved = theme;
                return Task.CompletedTask;
            }, TimeSpan.FromMilliseconds(25));
            int previews = 0;
            viewModel.ThemeChanged += (_, _) =>
            {
                previews++;
                cache.Get(imagePath);
            };

            for (int index = 0; index < 100; index++) viewModel.Opacity = index / 99d;
            Assert.Equal(100, previews);
            Assert.Equal(1d, viewModel.Opacity);

            await viewModel.FlushAsync();

            Assert.Equal(1, saves);
            Assert.Equal(1, decodes);
            Assert.Equal(1d, saved!.Opacity);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Flush_after_debounce_does_not_duplicate_the_latest_write()
    {
        string root = Directory.CreateTempSubdirectory("wordflow-theme-vm-").FullName;
        try
        {
            var service = await CreateServiceAsync(root);
            int saves = 0;
            var viewModel = new ThemeSettingsViewModel(service, (_, _) =>
            {
                Interlocked.Increment(ref saves);
                return Task.CompletedTask;
            }, TimeSpan.FromMilliseconds(15));

            viewModel.Opacity = .42;
            await Task.Delay(80);
            await viewModel.FlushAsync();

            Assert.Equal(1, saves);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Background_change_marshals_synchronously_to_the_owner_context()
    {
        string root = Directory.CreateTempSubdirectory("wordflow-theme-vm-").FullName;
        var previous = SynchronizationContext.Current;
        try
        {
            var service = await CreateServiceAsync(root);
            var owner = new RecordingSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(owner);
            var viewModel = new ThemeSettingsViewModel(service, (_, _) => Task.CompletedTask, TimeSpan.FromHours(1));
            bool eventObserved = false;
            viewModel.ThemeChanged += (_, _) => eventObserved = true;

            await Task.Run(() => viewModel.Opacity = .35);

            Assert.True(eventObserved);
            Assert.Equal(1, owner.SendCount);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Reset_cancels_a_pending_preview_write_before_its_durable_write()
    {
        string root = Directory.CreateTempSubdirectory("wordflow-theme-vm-").FullName;
        try
        {
            var service = await CreateServiceAsync(root);
            int saves = 0;
            var viewModel = new ThemeSettingsViewModel(service, async (theme, ct) =>
            {
                Interlocked.Increment(ref saves);
                await service.SaveAsync(theme, ct);
            }, TimeSpan.FromMilliseconds(50));

            viewModel.Opacity = .2;
            await viewModel.ResetAsync();
            await Task.Delay(100);
            await viewModel.FlushAsync();

            Assert.Equal(0, saves);
            Assert.Equal(ImageTheme.Default, await service.RestoreAsync());
            Assert.Equal(ImageTheme.Default, viewModel.Current);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Flush_waits_for_an_import_already_inside_durable_persistence()
    {
        string root = Directory.CreateTempSubdirectory("wordflow-theme-vm-").FullName;
        try
        {
            var service = await CreateServiceAsync(root);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var imported = new ImageTheme(Path.Combine(root, "imported.png"), .4);
            int obsoletePreviewSaves = 0;
            var viewModel = new ThemeSettingsViewModel(
                service,
                (_, _) =>
                {
                    obsoletePreviewSaves++;
                    return Task.CompletedTask;
                },
                TimeSpan.FromMilliseconds(10),
                importAsync: async (_, _, _) =>
                {
                    entered.SetResult();
                    await release.Task;
                    return imported;
                });

            Task import = viewModel.ImportAsync("source.png", .4);
            await entered.Task;
            viewModel.Opacity = .7;
            Task flush = viewModel.FlushAsync();

            Assert.False(flush.IsCompleted);
            release.SetResult();
            await Task.WhenAll(import, flush);
            Assert.Equal(imported, viewModel.Current);
            Assert.Equal(0, obsoletePreviewSaves);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Flush_waits_for_reset_and_a_later_preview_without_deadlock_or_duplicate_save()
    {
        string root = Directory.CreateTempSubdirectory("wordflow-theme-vm-").FullName;
        try
        {
            var service = await CreateServiceAsync(root);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int previewSaves = 0;
            ImageTheme? saved = null;
            var viewModel = new ThemeSettingsViewModel(
                service,
                (theme, _) =>
                {
                    previewSaves++;
                    saved = theme;
                    return Task.CompletedTask;
                },
                TimeSpan.FromMilliseconds(10),
                resetAsync: async _ =>
                {
                    entered.SetResult();
                    await release.Task;
                });

            Task reset = viewModel.ResetAsync();
            await entered.Task;
            Task firstFlush = viewModel.FlushAsync();
            Assert.False(firstFlush.IsCompleted);

            release.SetResult();
            await Task.WhenAll(reset, firstFlush).WaitAsync(TimeSpan.FromSeconds(2));
            viewModel.Opacity = .63;
            await viewModel.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, previewSaves);
            Assert.Equal(.63, saved!.Opacity);
            Assert.Equal(.63, viewModel.Current.Opacity);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Durable_sequence_does_not_start_current_until_a_faulted_predecessor_completes()
    {
        var predecessorRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool currentStarted = false;
        Task predecessor = Task.Run(async () =>
        {
            await predecessorRelease.Task;
            throw new InvalidOperationException("expected predecessor failure");
        });

        Task current = ThemeSettingsViewModel.RunAfterPredecessorAsync(predecessor, () =>
        {
            currentStarted = true;
            return Task.CompletedTask;
        });

        Assert.False(currentStarted);
        predecessorRelease.SetResult();
        await current.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(currentStarted);
    }

    [Fact]
    public async Task Durable_sequence_surfaces_the_current_operation_failure_to_its_caller()
    {
        var failure = new InvalidOperationException("current operation failed");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ThemeSettingsViewModel.RunAfterPredecessorAsync(
                Task.CompletedTask,
                () => Task.FromException(failure)));

        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task Later_reset_executes_after_held_import_and_flush_waits_through_both_in_order()
    {
        string root = Directory.CreateTempSubdirectory("wordflow-theme-vm-").FullName;
        try
        {
            var service = await CreateServiceAsync(root);
            var importEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var importRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resetEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resetRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var order = new List<string>();
            var imported = new ImageTheme(Path.Combine(root, "ordered-import.png"), .52);
            var viewModel = new ThemeSettingsViewModel(
                service,
                (_, _) => Task.CompletedTask,
                TimeSpan.FromMilliseconds(10),
                importAsync: async (_, _, _) =>
                {
                    order.Add("import-start");
                    importEntered.SetResult();
                    await importRelease.Task;
                    order.Add("import-end");
                    return imported;
                },
                resetAsync: async _ =>
                {
                    order.Add("reset-start");
                    resetEntered.SetResult();
                    await resetRelease.Task;
                    order.Add("reset-end");
                });

            Task import = viewModel.ImportAsync("source.png", imported.Opacity);
            await importEntered.Task;
            Task reset = viewModel.ResetAsync();
            Task flush = viewModel.FlushAsync();

            Assert.False(resetEntered.Task.IsCompleted);
            Assert.False(flush.IsCompleted);
            importRelease.SetResult();
            await resetEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(flush.IsCompleted);
            resetRelease.SetResult();

            await Task.WhenAll(import, reset, flush).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(["import-start", "import-end", "reset-start", "reset-end"], order);
            Assert.Equal(ImageTheme.Default, viewModel.Current);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<ImageThemeService> CreateServiceAsync(string root)
    {
        var paths = new AppPaths(Path.Combine(root, "local"), Path.Combine(root, "bundled"));
        paths.Initialize();
        var factory = new SqliteConnectionFactory(paths.UserDatabasePath);
        await new MigrationRunner(factory).MigrateAsync(default);
        return new ImageThemeService(paths, new SqliteAppSettingStore(factory));
    }

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        public int SendCount { get; private set; }
        public override void Send(SendOrPostCallback callback, object? state)
        {
            SendCount++;
            callback(state);
        }
    }
}
