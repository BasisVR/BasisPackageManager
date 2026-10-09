using Xunit;

namespace BasisPM.Cli.Tests;

public sealed class LineEditorTests
{
    private static LineBuffer Buffer(string text)
    {
        var buffer = new LineBuffer();
        buffer.Set(text);
        return buffer;
    }

    [Fact]
    public void EditsAtTheCursor()
    {
        var buffer = Buffer("stats");
        buffer.Left();
        buffer.Insert("u");
        Assert.Equal("status", buffer.Text);
        buffer.Home();
        buffer.Delete();
        Assert.Equal("tatus", buffer.Text);
        buffer.End();
        buffer.Backspace();
        Assert.Equal("tatu", buffer.Text);
        Assert.Equal(4, buffer.Cursor);
    }

    [Fact]
    public void MovesAndDeletesByWord()
    {
        var buffer = Buffer("install-package com.example.tool --yes");
        buffer.WordLeft();
        Assert.Equal(33, buffer.Cursor);
        buffer.DeleteWordBack();
        Assert.Equal("install-package --yes", buffer.Text);
        buffer.Home();
        buffer.WordRight();
        Assert.Equal(15, buffer.Cursor);
        buffer.KillToEnd();
        Assert.Equal("install-package", buffer.Text);
        buffer.KillToStart();
        Assert.Equal("", buffer.Text);
    }

    [Fact]
    public void OneCandidateCompletesTheWordAndAddsASpace()
    {
        var buffer = Buffer("upd");
        Assert.True(buffer.ApplyCompletion(new CompletionResult(0, new[] { "update-basis" })));
        Assert.Equal("update-basis ", buffer.Text);
    }

    [Fact]
    public void SeveralCandidatesCompleteTheirCommonPrefix()
    {
        var buffer = Buffer("server-u");
        Assert.False(buffer.ApplyCompletion(new CompletionResult(0, new[] { "server-update", "server-unlink" })));
        Assert.Equal("server-u", buffer.Text);
        buffer = Buffer("se");
        Assert.True(buffer.ApplyCompletion(new CompletionResult(0, new[] { "server-update", "server-unlink" })));
        Assert.Equal("server-u", buffer.Text);
        Assert.False(buffer.ApplyCompletion(new CompletionResult(0, new[] { "server-update", "server-unlink" })));
    }

    [Fact]
    public void CandidatesWithSpacesAreQuoted()
    {
        var buffer = Buffer("use Ma");
        Assert.True(buffer.ApplyCompletion(new CompletionResult(4, new[] { "Main LTS" })));
        Assert.Equal("use \"Main LTS\" ", buffer.Text);
        Assert.Equal(new[] { "use", "Main LTS" }, ConsoleApplication.Tokenize(buffer.Text));
    }

    [Fact]
    public void FolderCandidatesLeaveRoomToKeepTyping()
    {
        var separator = Path.DirectorySeparatorChar;
        var buffer = Buffer("projects add sr");
        Assert.True(buffer.ApplyCompletion(new CompletionResult(13, new[] { "src" + separator })));
        Assert.Equal("projects add src" + separator, buffer.Text);
        buffer = Buffer("projects add My");
        Assert.True(buffer.ApplyCompletion(new CompletionResult(13, new[] { "My Folder" + separator })));
        Assert.Equal("projects add \"My Folder\"", buffer.Text);
        buffer.Insert(separator + "x");
        Assert.Equal(new[] { "projects", "add", "My Folder" + separator + "x" }, ConsoleApplication.Tokenize(buffer.Text));
    }

    [Fact]
    public void HistorySkipsRepeatsAndKeepsSecretsOffDisk()
    {
        using var temp = new TempDir();
        var file = temp.Combine("history.txt");
        var editor = new LineEditor(file, _ => CompletionResult.None);
        editor.Remember("status");
        editor.Remember("status");
        editor.Remember("connect example.org --password hunter2");
        editor.Remember("  ");
        Assert.Equal(new[] { "status", "connect example.org --password hunter2" }, editor.History);
        Assert.Equal(new[] { "status" }, File.ReadAllLines(file));
    }

    [Theory]
    [InlineData("server-config ApiKey abc123", true)]
    [InlineData("connect example.org --password hunter2", true)]
    [InlineData("server-config SecretToken x", true)]
    [InlineData("install-package com.example.tool", false)]
    [InlineData("status", false)]
    public void SecretsStayOutOfTheHistoryFile(string line, bool sensitive) => Assert.Equal(sensitive, LineEditor.IsSensitive(line));
}
