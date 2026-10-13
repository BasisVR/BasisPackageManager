using System.Runtime.InteropServices;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class TerminalLauncherTests
{
    private static Func<string, bool> Installed(params string[] programs) => programs.ToHashSet(StringComparer.Ordinal).Contains;

    [Theory]
    [InlineData("/home/u/Basis", "'/home/u/Basis'")]
    [InlineData("/home/u/My Basis", "'/home/u/My Basis'")]
    [InlineData("/home/u/O'Neil's $HOME `x`", "'/home/u/O'\\''Neil'\\''s $HOME `x`'")]
    public void Quote_keeps_every_character_literal(string value, string expected) => Assert.Equal(expected, TerminalLauncher.Quote(value));

    [Fact]
    public void Script_records_its_pid_then_becomes_the_server()
    {
        var script = TerminalLauncher.Script("/srv/Basis Server/bin", "/srv/Basis Server/bin/BasisNetworkConsole", "/tmp/a.pid");
        Assert.Equal("#!/bin/sh\necho $$ > '/tmp/a.pid'\ncd '/srv/Basis Server/bin' || exit 1\nexec '/srv/Basis Server/bin/BasisNetworkConsole'\n", script);
    }

    [Fact]
    public void Script_exports_the_runtime_folder_before_starting()
    {
        var script = TerminalLauncher.Script("/s", "/s/server", "/p", new Dictionary<string, string> { ["DOTNET_ROOT"] = "/home/u/.dotnet", ["bad name"] = "x" });
        Assert.StartsWith("#!/bin/sh\necho $$ > '/p'\nexport DOTNET_ROOT='/home/u/.dotnet'\ncd '/s' || exit 1\n", script);
        Assert.DoesNotContain("bad name", script);
    }

    [Fact]
    public void macOS_opens_the_script_in_terminal()
    {
        var spec = Assert.Single(TerminalLauncher.Specs(OSPlatform.OSX, "/x/run.command", null, null, _ => false));
        Assert.Equal("open", spec.FileName);
        Assert.Equal(new[] { "-a", "Terminal", "/x/run.command" }, spec.Arguments);
    }

    [Fact]
    public void Windows_has_no_terminal_specs() => Assert.Empty(TerminalLauncher.Specs(OSPlatform.Windows, "x", null, null, _ => true));

    [Fact]
    public void Linux_prefers_the_users_choice_then_the_desktops_terminal()
    {
        var specs = TerminalLauncher.Specs(OSPlatform.Linux, "/s.sh", "kitty", "KDE", Installed("kitty", "x-terminal-emulator", "konsole", "xterm", "xdg-terminal-exec"));
        Assert.Equal(new[] { "xdg-terminal-exec", "kitty", "x-terminal-emulator", "konsole", "xterm" }, specs.Select(s => s.FileName));
        Assert.Equal(new[] { "/s.sh" }, specs[0].Arguments);
        Assert.Equal(new[] { "/s.sh" }, specs[1].Arguments);
        Assert.Equal(new[] { "-e", "/s.sh" }, specs[2].Arguments);
        Assert.Equal(new[] { "-e", "/s.sh" }, specs[3].Arguments);
    }

    [Fact]
    public void Gnome_terminals_take_the_script_after_a_double_dash()
    {
        var specs = TerminalLauncher.Specs(OSPlatform.Linux, "/s.sh", null, "ubuntu:GNOME", Installed("gnome-terminal", "ptyxis", "xfce4-terminal"));
        Assert.Equal(new[] { "ptyxis", "gnome-terminal", "xfce4-terminal" }, specs.Select(s => s.FileName));
        Assert.Equal(new[] { "--", "/s.sh" }, specs[0].Arguments);
        Assert.Equal(new[] { "-x", "/s.sh" }, specs[2].Arguments);
    }

    [Fact]
    public void An_unknown_terminal_variable_uses_dash_e_and_one_with_arguments_is_ignored()
    {
        Assert.Equal(new[] { "-e", "/s.sh" }, TerminalLauncher.Specs(OSPlatform.Linux, "/s.sh", "/opt/myterm", null, Installed("/opt/myterm"))[0].Arguments);
        Assert.Empty(TerminalLauncher.Specs(OSPlatform.Linux, "/s.sh", "foot --server", null, Installed("foot --server")));
    }

    [Fact]
    public void No_installed_terminal_means_no_specs() => Assert.Empty(TerminalLauncher.Specs(OSPlatform.Linux, "/s.sh", null, "GNOME", _ => false));
}
