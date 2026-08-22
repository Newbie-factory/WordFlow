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
