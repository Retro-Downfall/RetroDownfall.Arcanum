namespace RetroDownfall.Arcanum.Core.Pattern.Entities;

/// <summary>

/// Situational snapshot: inferred domain plus a bounded table of contents (signature lines).

/// </summary>

public sealed record PatternSnapshot(DomainType Domain, string RootPath, string[] Threads)
{

    private readonly string[] _threads = Threads ?? [];

    /// <summary>Signature lines; never null (a missing or explicitly-null <c>threads</c> normalizes to empty).</summary>
    public string[] Threads
    {

        get => _threads;

        init => _threads = value ?? [];

    }

}
