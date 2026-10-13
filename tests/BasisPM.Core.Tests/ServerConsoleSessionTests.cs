using System.Collections.Concurrent;
using System.Diagnostics;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class ServerConsoleSessionTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Streams_output_with_levels_and_takes_commands_until_it_is_shut_down()
    {
        using var t = new TempDir("console");
        var lines = new ConcurrentQueue<ServerConsoleLine>();
        using var session = ServerConsoleSession.Start(FakeServer(t), lines.Enqueue);

        await Until(() => lines.Any(l => l.Text.StartsWith("Admin UUID:", StringComparison.Ordinal)));
        Assert.Contains(lines, l => l.Text == "[12:00] [INFO] Server Booting" && l.Level == ServerConsoleLevel.Info);
        Assert.Contains(lines, l => l.Text == "[12:00] [WARNING] Careful" && l.Level == ServerConsoleLevel.Warning);
        Assert.Contains(lines, l => l.Text == "[12:00] [ERROR] Broke" && l.Level == ServerConsoleLevel.Error);
        Assert.Contains(lines, l => l.Text == "from stderr" && l.Level == ServerConsoleLevel.Error);
        Assert.Contains(lines, l => l.Text == "admin:did:key:first");

        Assert.True(session.SendLine("hello"));
        await Until(() => lines.Any(l => l.Text == "got:hello"));

        var code = await session.StopAsync(ServerConsoleSession.ShutdownCommand, Wait);
        Assert.Equal(0, code);
        Assert.True(session.HasExited);
        Assert.True(session.StopRequested);
        Assert.Equal(0, session.ExitCode);
        Assert.False(session.SendLine("too late"));
    }

    [Fact]
    public async Task Reports_the_exit_code_when_the_server_stops_by_itself()
    {
        using var t = new TempDir("console");
        var lines = new ConcurrentQueue<ServerConsoleLine>();
        using var session = ServerConsoleSession.Start(FakeServer(t), lines.Enqueue);
        await Until(() => lines.Any(l => l.Text.StartsWith("Admin UUID:", StringComparison.Ordinal)));

        Assert.True(session.SendLine("/crash"));
        var code = await session.Exited.WaitAsync(Wait);

        Assert.Equal(70, code);
        Assert.False(session.StopRequested);
    }

    [Fact]
    public async Task Ends_a_server_that_ignores_the_quit_command()
    {
        using var t = new TempDir("console");
        using var session = ServerConsoleSession.Start(StubbornServer(t));
        var watch = Stopwatch.StartNew();

        await session.StopAsync(ServerConsoleSession.ShutdownCommand, TimeSpan.FromMilliseconds(300)).WaitAsync(Wait);

        Assert.True(session.HasExited);
        Assert.True(session.StopRequested);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"Stopping took {watch.Elapsed}.");
    }

    [Theory]
    [InlineData("[06:40] [INFO] Server Booting", false, ServerConsoleLevel.Info)]
    [InlineData("[06:40] [WARNING] Slow tick", false, ServerConsoleLevel.Warning)]
    [InlineData("[06:40] [ERROR] Crashed", false, ServerConsoleLevel.Error)]
    [InlineData("plain text", false, ServerConsoleLevel.Info)]
    [InlineData("plain text", true, ServerConsoleLevel.Error)]
    [InlineData("an [ERROR] mid-line", false, ServerConsoleLevel.Info)]
    public void Levels_come_from_the_servers_log_tag(string text, bool fromError, ServerConsoleLevel expected)
    {
        Assert.Equal(expected, ServerConsoleSession.LevelOf(text, fromError));
    }

    [Fact]
    public void Clean_strips_terminal_codes_and_control_characters()
    {
        Assert.Equal("Booting done", ServerConsoleSession.Clean("\u001b[35mBooting\u001b[0m done"));
        Assert.Equal("tab\tkept", ServerConsoleSession.Clean("tab\tkept\u0007"));
        Assert.Equal("title gone", ServerConsoleSession.Clean("\u001b]0;Basis\u0007title gone"));
        Assert.Equal(ServerConsoleSession.MaxLineLength, ServerConsoleSession.Clean(new string('x', ServerConsoleSession.MaxLineLength + 50)).Length);
    }

    [Fact]
    public void First_run_environment_lists_admins_for_the_server()
    {
        Assert.Empty(BasisServerService.FirstRunEnvironment("  "));
        Assert.Empty(BasisServerService.FirstRunEnvironment(null));
        var environment = BasisServerService.FirstRunEnvironment(" did:key:a, did:key:b\ndid:key:c ");
        Assert.Equal("did:key:a,did:key:b,did:key:c", environment[BasisServerService.FirstAdminVariable]);
    }

    [Fact]
    public void First_run_and_console_input_follow_config_xml()
    {
        using var t = new TempDir("console");
        var service = new BasisServerService();
        var config = service.GetPaths(t.Path).ConfigFile;
        Assert.True(service.IsFirstRun(t.Path));
        Assert.True(service.AcceptsConsoleInput(t.Path));

        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "<Configuration><EnableConsole>false</EnableConsole></Configuration>");
        Assert.False(service.IsFirstRun(t.Path));
        Assert.False(service.AcceptsConsoleInput(t.Path));

        File.WriteAllText(config, "<Configuration><EnableConsole>true</EnableConsole></Configuration>");
        Assert.True(service.AcceptsConsoleInput(t.Path));
    }

    [Fact]
    public async Task StartConsole_runs_in_the_runtime_folder_with_the_extra_environment()
    {
        using var t = new TempDir("console");
        var service = new BasisServerService(paths => WithDirectory(FakeServer(t), paths.RuntimeDirectory));
        var lines = new ConcurrentQueue<ServerConsoleLine>();
        using var session = service.StartConsole(t.Path, lines.Enqueue, BasisServerService.FirstRunEnvironment("did:key:env"));

        await Until(() => lines.Any(l => l.Text == "admin:did:key:env"));
        Assert.True(Directory.Exists(service.GetPaths(t.Path).RuntimeDirectory));
        Assert.Equal(0, await session.StopAsync(ServerConsoleSession.ShutdownCommand, Wait));
    }

    internal static ProcessStartInfo FakeServer(TempDir t)
    {
        if (OperatingSystem.IsWindows())
        {
            var script = t.WriteFile("fake-server.cmd", string.Join("\r\n",
                "@echo off",
                "echo [12:00] [INFO] Server Booting",
                "echo [12:00] [WARNING] Careful",
                "echo [12:00] [ERROR] Broke",
                "1>&2 echo from stderr",
                "if not defined BasisFirstAdmin set BasisFirstAdmin=did:key:first",
                "echo admin:%BasisFirstAdmin%",
                "<nul set /p =Admin UUID: ",
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
        var shell = t.WriteFile("fake-server.sh", string.Join("\n",
            "echo '[12:00] [INFO] Server Booting'",
            "echo '[12:00] [WARNING] Careful'",
            "echo '[12:00] [ERROR] Broke'",
            "echo 'from stderr' >&2",
            "echo \"admin:${BasisFirstAdmin:-did:key:first}\"",
            "printf 'Admin UUID: '",
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

    private static ProcessStartInfo StubbornServer(TempDir t)
    {
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("cmd.exe");
            foreach (var argument in new[] { "/d", "/c", "ping -n 60 127.0.0.1 >nul" }) start.ArgumentList.Add(argument);
            return start;
        }
        var unix = new ProcessStartInfo("/bin/sh");
        unix.ArgumentList.Add(t.WriteFile("stubborn.sh", "sleep 60\n"));
        return unix;
    }

    private static ProcessStartInfo WithDirectory(ProcessStartInfo start, string directory)
    {
        start.WorkingDirectory = directory;
        return start;
    }

    private static async Task Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("The fake server didn't produce the expected output in time.");
            await Task.Delay(25);
        }
    }
}
