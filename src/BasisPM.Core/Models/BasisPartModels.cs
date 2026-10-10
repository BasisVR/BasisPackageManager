namespace BasisPM.Core.Models;

public sealed record BasisPart(string Id, string Name, string Folder, bool InUnityProject);

public enum BasisPartsMode { NotGitRepo, Complete, Managed, Custom }

public sealed record BasisPartState(BasisPart Part, string Path, bool Included, IReadOnlyList<string> Unsaved, int IgnoredFiles, IReadOnlyList<string> Repositories)
{
    public bool HasUnsavedWork => Unsaved.Count > 0;
}

public sealed record BasisPartsReport(BasisPartsMode Mode, string? RepositoryRoot, IReadOnlyList<BasisPartState> Parts)
{
    public bool CanChange => Mode is BasisPartsMode.Complete or BasisPartsMode.Managed;
    public IReadOnlyList<BasisPart> LeftOut => Parts.Where(p => !p.Included).Select(p => p.Part).ToList();
    public bool IsLeftOut(BasisPart part) => Parts.Any(p => p.Part == part && !p.Included);
}

public enum BasisPartsFailure { None, NotGitRepo, CustomSparseCheckout, OperationInProgress, UnsavedWork, GitFailed }

public sealed record BasisPartsResult(BasisPartsFailure Failure, string Detail, IReadOnlyList<BasisPart> Added, IReadOnlyList<BasisPart> Removed, int DeletedFiles, int IgnoredFilesLeft, IReadOnlyList<string> Kept)
{
    public bool Ok => Failure == BasisPartsFailure.None;
    public bool Changed => Added.Count + Removed.Count > 0;
    public static readonly BasisPartsResult Unchanged = new(BasisPartsFailure.None, "", Array.Empty<BasisPart>(), Array.Empty<BasisPart>(), 0, 0, Array.Empty<string>());
    public static BasisPartsResult Fail(BasisPartsFailure failure, string detail) => Unchanged with { Failure = failure, Detail = detail };
}
