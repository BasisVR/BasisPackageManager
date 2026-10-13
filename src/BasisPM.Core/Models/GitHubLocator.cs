namespace BasisPM.Core.Models;

public sealed record GitHubLocator(string Owner, string Repo, string? Branch, string? Path);
