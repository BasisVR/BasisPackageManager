using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class ServerConsoleTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    [AvaloniaFact]
    public void The_buffer_keeps_only_the_newest_lines()
    {
        var console = new ServerConsoleViewModel();
        for (var i = 0; i < ServerConsoleViewModel.MaxLines + 25; i++) console.Append(Line(ServerConsoleLevel.Info, "line " + i));
        console.Flush();

        Assert.Equal(ServerConsoleViewModel.MaxLines, console.Lines.Count);
        Assert.Equal("line 25", console.Lines[0].Text);
        Assert.Equal("line " + (ServerConsoleViewModel.MaxLines + 24), console.Lines[^1].Text);
    }

    [AvaloniaFact]
    public void The_panel_shows_only_while_a_server_runs_or_output_is_left()
    {
        var console = new ServerConsoleViewModel();
        Assert.False(console.IsVisible);

        console.IsRunning = true;
        Assert.True(console.IsVisible);
        console.IsRunning = false;
        Assert.False(console.IsVisible);

        console.Notice("hello");
        console.Flush();
        Assert.True(console.IsVisible);
        console.ClearCommand.Execute(null);
        Assert.False(console.IsVisible);
        Assert.False(console.HasLines);
    }

    [AvaloniaFact]
    public void Typing_needs_a_running_server_that_reads_input()
    {
        Localizer.Instance.SetLanguage("en");
        var console = new ServerConsoleViewModel();
        var sent = new List<string>();
        console.CommandEntered += sent.Add;
        console.Input = "/status";
        console.SendCommand.Execute(null);
        Assert.Empty(sent);

        console.IsRunning = true;
        console.AcceptsInput = false;
        Assert.False(console.CanType);
        Assert.Equal(L.Tr("server.console.inputOff"), console.InputHint);

        console.AcceptsInput = true;
        console.ExternalWindow = true;
        Assert.False(console.CanType);
        Assert.Equal(L.Tr("server.console.inputOwnWindow"), console.InputHint);

        console.ExternalWindow = false;
        Assert.True(console.CanType);
        console.SendCommand.Execute(null);
        Assert.Equal(new[] { "/status" }, sent);
        Assert.Equal("", console.Input);
    }

    [AvaloniaFact]
    public void Up_and_down_walk_the_command_history_and_keep_the_draft()
    {
        var console = new ServerConsoleViewModel { IsRunning = true };
        foreach (var command in new[] { "/help", "/status", "/status" })
        {
            console.Input = command;
            console.SendCommand.Execute(null);
        }
        console.Input = "/pla";

        console.Recall(-1);
        Assert.Equal("/status", console.Input);
        console.Recall(-1);
        Assert.Equal("/help", console.Input);
        console.Recall(-1);
        Assert.Equal("/help", console.Input);
        console.Recall(1);
        Assert.Equal("/status", console.Input);
        console.Recall(1);
        Assert.Equal("/pla", console.Input);
    }

    [AvaloniaFact]
    public void Copy_takes_every_line_in_order()
    {
        var console = new ServerConsoleViewModel();
        console.Append(Line(ServerConsoleLevel.Info, "one"));
        console.Append(Line(ServerConsoleLevel.Input, "/status"));
        console.Append(Line(ServerConsoleLevel.Error, "three"));
        console.Flush();

        Assert.Equal(string.Join(Environment.NewLine, "one", "> /status", "three"), console.AllText());
        Assert.NotSame(console.Lines[0].Brush, console.Lines[2].Brush);
    }

    [AvaloniaFact]
    public void Exit_notices_explain_the_servers_exit_codes()
    {
        Localizer.Instance.SetLanguage("en");
        Assert.Equal(L.Tr("server.console.notice.stopped"), ServerViewModel.ExitNotice(0, false));
        Assert.Equal(L.Tr("server.console.notice.stopped"), ServerViewModel.ExitNotice(137, true));
        Assert.Equal(L.Tr("server.console.notice.exitUnavailable", 69), ServerViewModel.ExitNotice(69, false));
        Assert.Equal(L.Tr("server.console.notice.exitCrashed", 70), ServerViewModel.ExitNotice(70, false));
        Assert.Equal(L.Tr("server.console.notice.exitConfig", 78), ServerViewModel.ExitNotice(78, false));
        Assert.Equal(L.Tr("server.console.notice.exited", 3), ServerViewModel.ExitNotice(3, false));
    }

    [AvaloniaFact]
    public async Task The_server_tab_runs_the_server_in_its_console()
    {
        Localizer.Instance.SetLanguage("en");
        var root = Path.Combine(Path.GetTempPath(), "basispm-tests", $"server-console-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Basis Server", "BasisServerConsole"));
            File.WriteAllText(Path.Combine(root, "Basis Server", "BasisServerConsole", "BasisNetworkConsole.csproj"), "<Project />");
            var service = new BasisServerService(paths =>
            {
                var script = FakeServer(root);
                script.WorkingDirectory = paths.RuntimeDirectory;
                return script;
            });
            var git = new GitService();
            var vm = new ServerViewModel(service, new ServerPackageService(git), new CatalogService(), new UserSettingsService(Path.Combine(root, "settings.json")),
                new VersionService(null, git), new BasisPartsService(git), new MainWindowViewModel());
            vm.SetActiveInstall(new BasisInstall { RepoRoot = root, UnityProjectPath = root, Name = "Console Test" });
            await vm.RefreshAsync();
            Assert.True(vm.IsFirstRun);
            Assert.False(vm.ServerConsole.IsVisible);

            vm.FirstRunAdmin = "did:key:vm";
            vm.RunCommand.Execute(null);
            await Until(() => vm.ServerConsole.Lines.Any(l => l.Text == "admin:did:key:vm"));
            Assert.True(vm.IsServerRunning);
            Assert.False(vm.IsFirstRun);
            Assert.False(vm.CanRunServer);
            Assert.True(vm.ServerConsole.CanType);
            Assert.Equal("", vm.FirstRunAdmin);
            Assert.Contains(vm.ServerConsole.Lines, l => l.Text == L.Tr("server.console.notice.firstRun"));
            Assert.Contains(vm.ServerConsole.Lines, l => l.Text == "[12:00] [WARNING] Careful" && l.Level == ServerConsoleLevel.Warning);

            vm.ServerConsole.Input = "hello";
            vm.ServerConsole.SendCommand.Execute(null);
            await Until(() => vm.ServerConsole.Lines.Any(l => l.Text == "got:hello"));
            Assert.Contains(vm.ServerConsole.Lines, l => l.Text == "> hello");

            vm.ServerConsole.Input = "/clear";
            vm.ServerConsole.SendCommand.Execute(null);
            Assert.Empty(vm.ServerConsole.Lines);

            vm.ServerConsole.Input = "/restart";
            vm.ServerConsole.SendCommand.Execute(null);
            await Until(() => vm.IsServerRunning && vm.ServerConsole.Lines.SkipWhile(l => l.Text != L.Tr("server.console.notice.restarting")).Any(l => l.Text.StartsWith("admin:", StringComparison.Ordinal)));
            Assert.Contains(vm.ServerConsole.Lines, l => l.Text == L.Tr("server.console.notice.stopped"));

            vm.StopCommand.Execute(null);
            await Until(() => !vm.IsServerRunning && vm.ServerConsole.Lines.Any(l => l.Text == L.Tr("server.console.notice.stopped")));
            Assert.Equal(L.Tr("server.console.status.stopped"), vm.ServerConsole.Status);
            Assert.True(vm.CanRunServer);

            vm.RunCommand.Execute(null);
            await Until(() => vm.IsServerRunning && vm.ServerConsole.Lines.Count(l => l.Text.StartsWith("admin:", StringComparison.Ordinal)) == 1);
            vm.ServerConsole.Input = "/crash";
            vm.ServerConsole.SendCommand.Execute(null);
            await Until(() => !vm.IsServerRunning && vm.ServerConsole.Lines.Any(l => l.Text == L.Tr("server.console.notice.exitCrashed", 70)));
            Assert.Equal(L.Tr("server.console.status.exited", 70), vm.ServerConsole.Status);
            Assert.Equal(ServerConsoleLevel.Error, vm.ServerConsole.Lines.Last().Level);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); }
            catch (IOException) { }
        }
    }

    [AvaloniaFact]
    public void Running_an_unbuilt_server_only_reports_it()
    {
        Localizer.Instance.SetLanguage("en");
        var root = Path.Combine(Path.GetTempPath(), "basispm-tests", $"server-unbuilt-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Basis Server", "BasisServerConsole"));
            File.WriteAllText(Path.Combine(root, "Basis Server", "BasisServerConsole", "BasisNetworkConsole.csproj"), "<Project />");
            var shell = new MainWindowViewModel();
            var vm = shell.ServerVM;
            vm.SetActiveInstall(new BasisInstall { RepoRoot = root, UnityProjectPath = root, Name = "Unbuilt" });

            vm.RunCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            Assert.False(vm.IsServerRunning);
            Assert.False(vm.ServerConsole.IsVisible);
            Assert.Equal(L.Tr("server.console.startFailed", L.Tr("server.console.notBuilt")), shell.StatusMessage);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); }
            catch (IOException) { }
        }
    }

    private static ServerConsoleLine Line(ServerConsoleLevel level, string text) => new(DateTimeOffset.Now, level, text);

    private static ProcessStartInfo FakeServer(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(root, "fake-server.cmd");
            File.WriteAllText(script, string.Join("\r\n",
                "@echo off",
                "echo [12:00] [INFO] Server Booting",
                "echo [12:00] [WARNING] Careful",
                "if not defined BasisFirstAdmin set BasisFirstAdmin=none",
                "echo admin:%BasisFirstAdmin%",
                ":loop",
                "set \"line=\"",
                "ver >nul",
                "set /p \"line=\"",
                "if errorlevel 1 exit /b 5",
                "if /i \"%line%\"==\"/shutdown\" exit /b 0",
                "if /i \"%line%\"==\"/crash\" exit /b 70",
                "echo got:%line%",
                "goto loop",
                ""));
            var start = new ProcessStartInfo("cmd.exe");
            foreach (var argument in new[] { "/d", "/c", script }) start.ArgumentList.Add(argument);
            return start;
        }
        var shell = Path.Combine(root, "fake-server.sh");
        File.WriteAllText(shell, string.Join("\n",
            "echo '[12:00] [INFO] Server Booting'",
            "echo '[12:00] [WARNING] Careful'",
            "echo \"admin:${BasisFirstAdmin:-none}\"",
            "while IFS= read -r line; do",
            "  case \"$line\" in",
            "    /shutdown) exit 0 ;;",
            "    /crash) exit 70 ;;",
            "  esac",
            "  echo \"got:$line\"",
            "done",
            "exit 5",
            ""));
        var unix = new ProcessStartInfo("/bin/sh");
        unix.ArgumentList.Add(shell);
        return unix;
    }

    private static async Task Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            if (DateTime.UtcNow > until) throw new TimeoutException("The server console didn't reach the expected state in time.");
            await Task.Delay(25);
        }
    }
}
