namespace RetroDownfall.Arcanum.Core.Covenant;

public interface ICovenantCompiler
{
    CovenantCompiledContent Compile(string key, string authoredContent);

    /// <summary>
    /// Renders a Proposed block from freshly compiled fragments, using the fence length each one
    /// persists.
    /// </summary>
    /// <remarks>
    /// Not the renderer a prompt sees. A live turn renders from frozen fragment bytes that were
    /// persisted turns or sessions earlier, and rescans them rather than trusting a stored number, so
    /// <c>CovenantSectionRenderer</c> owns that path. This one answers the same question from the
    /// compile side, which is what makes the persisted fence length auditable at all. The two are
    /// held byte-for-byte together by test, because a fence shorter than the payload's own longest
    /// backtick run is a fence the payload closes early.
    /// </remarks>
    string RenderProposedSection(IReadOnlyList<CovenantCompiledContent> fragments);
}

public sealed class CovenantCompiledContent
{
    private readonly byte[] _authoredUtf8;

    private readonly byte[] _fragmentUtf8;

    internal CovenantCompiledContent(
        string normalizedKey,
        string authoredContent,
        byte[] authoredUtf8,
        string fragment,
        byte[] fragmentUtf8,
        int requiredFenceLength,
        CovenantDigest authoredHash,
        CovenantDigest fragmentHash,
        int compilerPolicyVersion,
        int rendererPolicyVersion)
    {
        NormalizedKey = normalizedKey;
        AuthoredContent = authoredContent;
        _authoredUtf8 = authoredUtf8;
        Fragment = fragment;
        _fragmentUtf8 = fragmentUtf8;
        RequiredFenceLength = requiredFenceLength;
        AuthoredHash = authoredHash;
        FragmentHash = fragmentHash;
        CompilerPolicyVersion = compilerPolicyVersion;
        RendererPolicyVersion = rendererPolicyVersion;
    }

    public string NormalizedKey { get; }

    public string AuthoredContent { get; }

    public byte[] AuthoredUtf8 => _authoredUtf8.ToArray();

    public int AuthoredUtf8ByteCount => _authoredUtf8.Length;

    public string Fragment { get; }

    public byte[] FragmentUtf8 => _fragmentUtf8.ToArray();

    public int FragmentUtf8ByteCount => _fragmentUtf8.Length;

    public int RequiredFenceLength { get; }

    public CovenantDigest AuthoredHash { get; }

    public CovenantDigest FragmentHash { get; }

    public int CompilerPolicyVersion { get; }

    public int RendererPolicyVersion { get; }
}

public sealed record CovenantCompilerCorpusResult(
    bool Succeeded,
    int NormalizationCaseCount,
    int NormalizationAssertionCount,
    int FormatScalarCount,
    int FormatAdjacentScalarCount,
    int ProhibitedControlCount,
    int MalformedUtf16CaseCount,
    int AcceptedKeyCaseCount,
    int RejectedKeyCaseCount,
    int Utf8BoundaryCaseCount,
    int WhitespaceScalarCount,
    int RendererCaseCount,
    int VectorCount,
    CovenantDigest AggregateHash);
