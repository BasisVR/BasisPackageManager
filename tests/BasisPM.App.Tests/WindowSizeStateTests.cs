using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using BasisPM.App.Views;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class WindowSizeStateTests
{
    [AvaloniaFact]
    public async Task The_window_opens_at_the_size_it_was_left()
    {
        var path = NewPath();
        var service = new UserSettingsService(path);
        try
        {
            var window = Open(service, new UserSettings { WindowWidth = 1000, WindowHeight = 700 });
            Assert.Equal(new Size(1000, 700), window.ClientSize);

            Resize(window, 1300, 900);
            var saved = await SavedAsync(service, s => s.WindowWidth == 1300);
            Assert.Equal(900, saved.WindowHeight);
            Assert.False(saved.WindowMaximized);
            window.Close();

            var again = Open(service, service.Load());
            Assert.Equal(new Size(1300, 900), again.ClientSize);
            again.Close();
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task Maximizing_keeps_the_normal_size_and_reopens_maximized()
    {
        var path = NewPath();
        var service = new UserSettingsService(path);
        try
        {
            var window = Open(service, new UserSettings { WindowWidth = 1000, WindowHeight = 700 });
            Resize(window, 1900, 1200);
            window.WindowState = WindowState.Maximized;
            var saved = await SavedAsync(service, s => s.WindowMaximized);
            Assert.Equal(1000, saved.WindowWidth);
            Assert.Equal(700, saved.WindowHeight);
            window.Close();

            var again = Prepare(service, service.Load());
            Assert.Equal(WindowState.Maximized, again.WindowState);
            Assert.Equal(1000, again.Width);
            Assert.Equal(700, again.Height);
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public void Closing_right_after_a_resize_still_saves_it()
    {
        var path = NewPath();
        var service = new UserSettingsService(path);
        try
        {
            var window = Open(service, new UserSettings { WindowWidth = 1000, WindowHeight = 700, CatalogUrl = "at startup" });
            service.Update(s => s.CatalogUrl = "changed while open");
            Resize(window, 1111, 777);
            window.Close();

            var saved = service.Load();
            Assert.Equal(1111, saved.WindowWidth);
            Assert.Equal(777, saved.WindowHeight);
            Assert.Equal("changed while open", saved.CatalogUrl);
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task Closing_while_the_settings_are_busy_does_not_hang()
    {
        var path = NewPath();
        var service = new UserSettingsService(path);
        try
        {
            var window = Open(service, new UserSettings { WindowWidth = 1000, WindowHeight = 700 });
            Resize(window, 1111, 777);
            using var release = new ManualResetEventSlim();
            var held = new TaskCompletionSource();
            var busy = Task.Run(() => service.Update(_ => { held.SetResult(); release.Wait(TimeSpan.FromSeconds(10)); }));
            await held.Task;

            var clock = Stopwatch.StartNew();
            window.Close();
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Closing took {clock.Elapsed}");

            release.Set();
            await busy;
            var saved = await SavedAsync(service, s => s.WindowWidth == 1111);
            Assert.Equal(777, saved.WindowHeight);
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public void A_size_bigger_than_the_screen_is_fitted_to_it()
    {
        var window = Prepare(null, new UserSettings { WindowWidth = 50000, WindowHeight = 40000 });
        var screen = window.Screens.Primary!;
        var area = screen.WorkingArea.ToRect(screen.Scaling);

        Assert.Equal(area.Width, window.Width);
        Assert.Equal(area.Height, window.Height);
    }

    [AvaloniaFact]
    public void Unusable_saved_sizes_keep_the_default()
    {
        foreach (var saved in new[]
        {
            new UserSettings(),
            new UserSettings { WindowWidth = 0, WindowHeight = 700 },
            new UserSettings { WindowWidth = 1000, WindowHeight = -1 },
            new UserSettings { WindowWidth = double.NaN, WindowHeight = 700 },
        })
        {
            var window = Prepare(null, saved);
            Assert.Equal(1240, window.Width);
            Assert.Equal(820, window.Height);
            Assert.Equal(WindowState.Normal, window.WindowState);
        }
    }

    private static Window Prepare(UserSettingsService? service, UserSettings saved)
    {
        var window = new Window { Width = 1240, Height = 820 };
        WindowSizeState.Attach(window, service, saved);
        return window;
    }

    private static Window Open(UserSettingsService? service, UserSettings saved)
    {
        var window = Prepare(service, saved);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void Resize(Window window, double width, double height)
    {
        window.Width = width;
        window.Height = height;
        Dispatcher.UIThread.RunJobs();
    }

    private static string NewPath() => Path.Combine(Path.GetTempPath(), $"basispm-window-{Guid.NewGuid():N}.json");

    private static async Task<UserSettings> SavedAsync(UserSettingsService service, Func<UserSettings, bool> done)
    {
        for (var i = 0; i < 200; i++)
        {
            var settings = await service.LoadAsync();
            if (done(settings)) return settings;
            await Task.Delay(25);
        }
        return await service.LoadAsync();
    }
}
