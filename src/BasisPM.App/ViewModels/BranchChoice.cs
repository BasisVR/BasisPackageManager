namespace BasisPM.App.ViewModels;

public sealed record BranchChoice(string Label, string Name, string? Remote, bool IsBasis, bool IsCurrent);

public sealed record BranchGroup(string Title, string Hint, IReadOnlyList<BranchChoice> Items)
{
    public bool HasTitle => Title.Length > 0;
    public bool HasHint => Hint.Length > 0;
}
