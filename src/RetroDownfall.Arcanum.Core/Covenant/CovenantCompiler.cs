using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RetroDownfall.Arcanum.Core.Covenant;

public sealed class CovenantCompiler : ICovenantCompiler
{
    public const int CompilerPolicyVersion = 1;

    public const int RendererPolicyVersion = 1;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public CovenantCompiledContent Compile(string key, string authoredContent)
    {
        CovenantKey normalizedKey = new(key);

        ArgumentNullException.ThrowIfNull(authoredContent);

        byte[] authoredUtf8 = EncodeAuthored(authoredContent);

        ValidateAuthoredScalars(authoredContent);

        string normalized = CovenantUnicodePolicyV1.NormalizeToNfc(authoredContent);
        string collapsed = CollapseWhitespace(normalized);

        if (collapsed.Length == 0)
        {
            throw new ArgumentException("Covenant authored content cannot be empty or whitespace-only.", nameof(authoredContent));
        }

        string fragment = RenderFragment(normalizedKey.Value, collapsed);
        byte[] fragmentUtf8 = StrictUtf8.GetBytes(fragment);
        int requiredFenceLength = GetRequiredFenceLength(collapsed);
        CovenantDigest authoredHash = HashCanonicalArtifact(
            CovenantDomainTag.Authored,
            CompilerPolicyVersion,
            normalizedKey.Value,
            authoredUtf8);
        CovenantDigest fragmentHash = HashCanonicalArtifact(
            CovenantDomainTag.Fragment,
            RendererPolicyVersion,
            normalizedKey.Value,
            fragmentUtf8);

        return new CovenantCompiledContent(
            normalizedKey.Value,
            authoredContent,
            authoredUtf8,
            fragment,
            fragmentUtf8,
            requiredFenceLength,
            authoredHash,
            fragmentHash,
            CompilerPolicyVersion,
            RendererPolicyVersion);
    }

    public string RenderProposedSection(IReadOnlyList<CovenantCompiledContent> fragments)
    {
        ArgumentNullException.ThrowIfNull(fragments);

        if (fragments.Count == 0)
        {
            throw new ArgumentException("A Proposed Covenant section requires at least one compiled fragment.", nameof(fragments));
        }

        int fenceLength = 3;
        int characterCount = checked((fenceLength * 2) + "text\n".Length + 1);

        for (int index = 0; index < fragments.Count; index++)
        {
            CovenantCompiledContent fragment = fragments[index]
                ?? throw new ArgumentException("A Proposed Covenant section cannot contain a null fragment.", nameof(fragments));

            fenceLength = Math.Max(fenceLength, fragment.RequiredFenceLength);
            characterCount = checked(characterCount + fragment.Fragment.Length);
        }

        characterCount = checked(characterCount + ((fenceLength - 3) * 2));
        StringBuilder section = new(characterCount);
        string fence = new('`', fenceLength);

        section.Append(fence);
        section.Append("text\n");

        for (int index = 0; index < fragments.Count; index++)
        {
            section.Append(fragments[index].Fragment);
        }

        section.Append(fence);
        section.Append('\n');

        if (section.Length != characterCount)
        {
            throw new InvalidOperationException("The Proposed Covenant section did not match its checked size.");
        }

        return section.ToString();
    }

    private static byte[] EncodeAuthored(string authoredContent)
    {
        int byteCount;

        try
        {
            byteCount = StrictUtf8.GetByteCount(authoredContent);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("Covenant authored content must contain valid Unicode scalar values.", nameof(authoredContent), exception);
        }

        if (byteCount > CovenantLimits.MaxAuthoredContentBytes)
        {
            throw new ArgumentException(
                $"Covenant authored content cannot exceed {CovenantLimits.MaxAuthoredContentBytes} strict UTF-8 bytes.",
                nameof(authoredContent));
        }

        byte[] encoded = new byte[byteCount];

        try
        {
            StrictUtf8.GetBytes(authoredContent.AsSpan(), encoded);
        }
        catch (EncoderFallbackException exception)
        {
            encoded.AsSpan().Clear();
            throw new ArgumentException("Covenant authored content must contain valid Unicode scalar values.", nameof(authoredContent), exception);
        }

        return encoded;
    }

    private static void ValidateAuthoredScalars(string authoredContent)
    {
        ReadOnlySpan<char> remaining = authoredContent.AsSpan();

        while (!remaining.IsEmpty)
        {
            OperationStatus status = Rune.DecodeFromUtf16(remaining, out Rune rune, out int charsConsumed);

            if (status != OperationStatus.Done)
            {
                throw new ArgumentException("Covenant authored content must contain valid Unicode scalar values.", nameof(authoredContent));
            }

            int scalar = rune.Value;

            if (IsProhibitedControl(scalar))
            {
                throw new ArgumentException("Covenant authored content contains a prohibited control scalar.", nameof(authoredContent));
            }

            if (CovenantUnicodePolicyV1.IsFormatScalar(scalar))
            {
                throw new ArgumentException("Covenant authored content contains a Unicode 17 Format scalar.", nameof(authoredContent));
            }

            remaining = remaining[charsConsumed..];
        }
    }

    private static bool IsProhibitedControl(int scalar) =>
        scalar is >= 0x00 and <= 0x1f && scalar is not 0x09 and not 0x0a and not 0x0d
            || scalar is >= 0x7f and <= 0x9f;

    private static string CollapseWhitespace(string normalized)
    {
        char[] characters = new char[normalized.Length];
        ReadOnlySpan<char> remaining = normalized.AsSpan();
        int written = 0;
        bool pendingSpace = false;

        try
        {
            while (!remaining.IsEmpty)
            {
                OperationStatus status = Rune.DecodeFromUtf16(remaining, out Rune rune, out int charsConsumed);

                if (status != OperationStatus.Done)
                {
                    throw new InvalidOperationException("Unicode policy output contained malformed UTF-16.");
                }

                if (CovenantUnicodePolicyV1.IsWhitespaceScalar(rune.Value))
                {
                    pendingSpace = written > 0;
                    remaining = remaining[charsConsumed..];
                    continue;
                }

                if (pendingSpace)
                {
                    characters[written] = ' ';
                    written++;
                    pendingSpace = false;
                }

                remaining[..charsConsumed].CopyTo(characters.AsSpan(written));
                written = checked(written + charsConsumed);
                remaining = remaining[charsConsumed..];
            }

            return new string(characters, 0, written);
        }
        finally
        {
            characters.AsSpan().Clear();
        }
    }

    private static string RenderFragment(string key, string content)
    {
        int escapeCount = 0;

        foreach (char character in content)
        {
            if (character is '\\' or '"')
            {
                escapeCount = checked(escapeCount + 1);
            }
        }

        int characterCount = checked(2 + key.Length + 3 + content.Length + escapeCount + 2);
        char[] fragment = new char[characterCount];
        int written = 0;

        try
        {
            fragment[written++] = '-';
            fragment[written++] = ' ';
            key.AsSpan().CopyTo(fragment.AsSpan(written));
            written += key.Length;
            fragment[written++] = ':';
            fragment[written++] = ' ';
            fragment[written++] = '"';

            foreach (char character in content)
            {
                if (character is '\\' or '"')
                {
                    fragment[written++] = '\\';
                }

                fragment[written++] = character;
            }

            fragment[written++] = '"';
            fragment[written++] = '\n';

            if (written != characterCount)
            {
                throw new InvalidOperationException("The compiled Covenant fragment did not match its checked size.");
            }

            return new string(fragment);
        }
        finally
        {
            fragment.AsSpan().Clear();
        }
    }

    private static int GetRequiredFenceLength(string content)
    {
        int longestRun = 0;
        int currentRun = 0;

        foreach (char character in content)
        {
            if (character == '`')
            {
                currentRun = checked(currentRun + 1);
                longestRun = Math.Max(longestRun, currentRun);
            }
            else
            {
                currentRun = 0;
            }
        }

        return Math.Max(3, checked(longestRun + 1));
    }

    /// <summary>
    /// Recomputes the authored-content hash for a stored version.
    /// </summary>
    /// <remarks>
    /// The canonical loader has the stored bytes and the stored hash but not the object that
    /// produced them, so verification needs the same preimage the compiler used. Exposing the two
    /// hash functions is strictly safer than letting the loader assemble a preimage of its own that
    /// could drift from this one.
    /// </remarks>
    public static CovenantDigest HashAuthored(string normalizedKey, ReadOnlySpan<byte> authoredUtf8)
    {
        ArgumentNullException.ThrowIfNull(normalizedKey);

        return HashCanonicalArtifact(
            CovenantDomainTag.Authored,
            CompilerPolicyVersion,
            normalizedKey,
            authoredUtf8);
    }

    /// <summary>
    /// Recomputes the compiled-fragment hash for a stored version.
    /// </summary>
    public static CovenantDigest HashFragment(string normalizedKey, ReadOnlySpan<byte> fragmentUtf8)
    {
        ArgumentNullException.ThrowIfNull(normalizedKey);

        return HashCanonicalArtifact(
            CovenantDomainTag.Fragment,
            RendererPolicyVersion,
            normalizedKey,
            fragmentUtf8);
    }

    private static CovenantDigest HashCanonicalArtifact(
        CovenantDomainTag domainTag,
        int policyVersion,
        string key,
        ReadOnlySpan<byte> artifact)
    {
        string tag = CovenantPolicyV1Manifest.GetDomainTag(domainTag);
        int maximumBytes = checked(
            tag.Length
            + 1
            + sizeof(uint)
            + sizeof(uint)
            + key.Length
            + sizeof(uint)
            + artifact.Length);
        CovenantCanonicalEncoder encoder = new(maximumBytes);

        encoder.WriteDomainTag(domainTag);
        encoder.WriteUInt32(checked((uint)policyVersion));
        encoder.WriteUtf8(key);
        encoder.WriteBytes(artifact);

        if (encoder.WrittenCount != maximumBytes)
        {
            throw new InvalidOperationException("The Covenant artifact preimage did not match its checked size.");
        }

        return new CovenantDigest(SHA256.HashData(encoder.WrittenSpan));
    }
}

public static class CovenantCompilerCorpus
{
    private const int ExpectedCorpusBytes = 815_612;

    private const int ExpectedNormalizationCases = 20_034;

    private const int ExpectedNormalizationAssertions = ExpectedNormalizationCases * 5;

    private const int ExpectedFormatRangeCount = 21;

    private const int ExpectedFormatScalarCount = 170;

    private const int ExpectedFormatAdjacentScalarCount = ExpectedFormatRangeCount * 2;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static ReadOnlySpan<byte> ExpectedCorpusHash =>
    [
        0x02, 0xaf, 0xa4, 0x21, 0x85, 0x7a, 0x07, 0x5f,
        0x29, 0xac, 0x88, 0x8b, 0x38, 0x24, 0xb4, 0x1f,
        0xa8, 0x2e, 0x10, 0x7b, 0x0b, 0x66, 0x92, 0x1a,
        0x95, 0xea, 0x2d, 0x20, 0x98, 0x9e, 0xf8, 0x38
    ];

    private static ReadOnlySpan<byte> ExpectedNormalizationSourceHash =>
    [
        0x50, 0x19, 0xff, 0xd5, 0x30, 0x75, 0x1a, 0x74,
        0x19, 0x00, 0xc8, 0x49, 0xc0, 0xe0, 0x10, 0x33,
        0x2f, 0x14, 0x2a, 0x36, 0x12, 0x23, 0x46, 0x39,
        0xbd, 0x20, 0x0b, 0x82, 0x13, 0x8a, 0x87, 0xdb
    ];

    private static ReadOnlySpan<ulong> ExpectedFormatRanges =>
    [
        0x0000000015A000ADUL, 0x00000000C0000605UL, 0x00000000C380061CUL, 0x00000000DBA006DDUL,
        0x00000000E1E0070FUL, 0x0000000112000891UL, 0x000000011C4008E2UL, 0x0000000301C0180EUL,
        0x000000040160200FUL, 0x000000040540202EUL, 0x000000040C002064UL, 0x000000040CC0206FUL,
        0x0000001FDFE0FEFFUL, 0x0000001FFF20FFFBUL, 0x0000002217A110BDUL, 0x0000002219A110CDUL,
        0x000000268601343FUL, 0x000000379401BCA3UL, 0x0000003A2E61D17AUL, 0x000001C0002E0001UL,
        0x000001C0040E007FUL
    ];

    public static CovenantCompilerCorpusResult Run(ReadOnlySpan<byte> compactCorpus)
    {
        Span<byte> corpusHash = stackalloc byte[CovenantLimits.DigestBytes];

        ValidateCompactCorpus(compactCorpus, corpusHash);

        CovenantCompiler compiler = new();
        int offset = 40;
        int normalizationCaseCount = checked((int)ReadUInt32(compactCorpus, ref offset));
        int normalizationAssertionCount = 0;
        int previousLineNumber = 0;
        bool succeeded = normalizationCaseCount == ExpectedNormalizationCases;

        succeeded &= ExpectedFormatRanges.Length == ExpectedFormatRangeCount;
        succeeded &= HasExpectedFormatRanges(CovenantUnicode17Tables.FormatRanges);

        for (int index = 0; index < normalizationCaseCount; index++)
        {
            int lineNumber = checked((int)ReadUInt32(compactCorpus, ref offset));
            string c1 = ReadUtf8(compactCorpus, ref offset);
            string c2 = ReadUtf8(compactCorpus, ref offset);
            string c3 = ReadUtf8(compactCorpus, ref offset);
            string c4 = ReadUtf8(compactCorpus, ref offset);
            string c5 = ReadUtf8(compactCorpus, ref offset);

            succeeded &= lineNumber > previousLineNumber;
            previousLineNumber = lineNumber;
            succeeded &= MatchesNormalization(c2, c1);
            normalizationAssertionCount++;
            succeeded &= MatchesNormalization(c2, c2);
            normalizationAssertionCount++;
            succeeded &= MatchesNormalization(c2, c3);
            normalizationAssertionCount++;
            succeeded &= MatchesNormalization(c4, c4);
            normalizationAssertionCount++;
            succeeded &= MatchesNormalization(c4, c5);
            normalizationAssertionCount++;
        }

        succeeded &= offset == compactCorpus.Length;
        succeeded &= normalizationAssertionCount == ExpectedNormalizationAssertions;

        int formatScalarCount = RunFormatScalarCases(compiler, ExpectedFormatRanges, ref succeeded);
        int formatAdjacentScalarCount = RunFormatAdjacentCases(compiler, ExpectedFormatRanges, ref succeeded);
        int prohibitedControlCount = RunProhibitedControlCases(compiler, ref succeeded);
        int malformedUtf16CaseCount = RunMalformedUtf16Cases(compiler, ref succeeded);
        (int acceptedKeyCaseCount, int rejectedKeyCaseCount) = RunKeyCases(compiler, ref succeeded);
        int utf8BoundaryCaseCount = RunUtf8BoundaryCases(compiler, ref succeeded);
        int whitespaceScalarCount = RunWhitespaceCases(compiler, ref succeeded);
        int rendererCaseCount = RunRendererCases(compiler, ref succeeded);
        CovenantCompiledContent[] vectors = CompilePublishedVectors(compiler, out bool vectorsSucceeded);

        succeeded &= vectorsSucceeded;
        succeeded &= formatScalarCount == ExpectedFormatScalarCount;
        succeeded &= formatAdjacentScalarCount == ExpectedFormatAdjacentScalarCount;

        CovenantDigest formatRangeIdentityHash = ComputeExpectedFormatRangeIdentityHash();

        CovenantDigest aggregate = ComputeCompleteAggregate(
            corpusHash,
            formatRangeIdentityHash,
            normalizationCaseCount,
            normalizationAssertionCount,
            formatScalarCount,
            formatAdjacentScalarCount,
            prohibitedControlCount,
            malformedUtf16CaseCount,
            acceptedKeyCaseCount,
            rejectedKeyCaseCount,
            utf8BoundaryCaseCount,
            whitespaceScalarCount,
            rendererCaseCount,
            vectors,
            succeeded);

        corpusHash.Clear();

        return new CovenantCompilerCorpusResult(
            succeeded,
            normalizationCaseCount,
            normalizationAssertionCount,
            formatScalarCount,
            formatAdjacentScalarCount,
            prohibitedControlCount,
            malformedUtf16CaseCount,
            acceptedKeyCaseCount,
            rejectedKeyCaseCount,
            utf8BoundaryCaseCount,
            whitespaceScalarCount,
            rendererCaseCount,
            vectors.Length,
            aggregate);
    }

    private static void ValidateCompactCorpus(
        ReadOnlySpan<byte> compactCorpus,
        Span<byte> corpusHash)
    {
        if (compactCorpus.Length != ExpectedCorpusBytes)
        {
            throw new ArgumentException(
                $"The Covenant compiler corpus must contain exactly {ExpectedCorpusBytes} bytes.",
                nameof(compactCorpus));
        }

        if (!compactCorpus[..8].SequenceEqual("ARCUNFC1"u8))
        {
            throw new ArgumentException("The Covenant compiler corpus magic is invalid.", nameof(compactCorpus));
        }

        if (!compactCorpus.Slice(8, CovenantLimits.DigestBytes).SequenceEqual(ExpectedNormalizationSourceHash))
        {
            throw new ArgumentException(
                "The Covenant compiler corpus normalization-source hash is invalid.",
                nameof(compactCorpus));
        }

        uint caseCount = BinaryPrimitives.ReadUInt32BigEndian(compactCorpus[40..]);

        if (caseCount != ExpectedNormalizationCases)
        {
            throw new ArgumentException(
                $"The Covenant compiler corpus must contain exactly {ExpectedNormalizationCases} cases.",
                nameof(compactCorpus));
        }

        int written = SHA256.HashData(compactCorpus, corpusHash);

        if (written != CovenantLimits.DigestBytes || !corpusHash.SequenceEqual(ExpectedCorpusHash))
        {
            corpusHash.Clear();
            throw new ArgumentException("The Covenant compiler corpus artifact hash is invalid.", nameof(compactCorpus));
        }
    }

    internal static bool HasExpectedFormatRanges(ReadOnlySpan<ulong> formatRanges) =>
        formatRanges.SequenceEqual(ExpectedFormatRanges);

    internal static CovenantDigest ComputeExpectedFormatRangeIdentityHash()
    {
        ReadOnlySpan<byte> domain = "Arcanum.Covenant.CompilerCorpus.FormatRanges.v1\0"u8;
        ReadOnlySpan<ulong> formatRanges = ExpectedFormatRanges;
        Span<byte> evidence = stackalloc byte[domain.Length + sizeof(uint) + (ExpectedFormatRangeCount * sizeof(ulong))];
        int offset = 0;

        domain.CopyTo(evidence);
        offset += domain.Length;
        WriteUInt32(evidence, ref offset, formatRanges.Length);

        foreach (ulong packed in formatRanges)
        {
            WriteUInt32(evidence, ref offset, checked((int)(packed >> 21)));
            WriteUInt32(evidence, ref offset, checked((int)(packed & 0x1fffff)));
        }

        if (offset != evidence.Length)
        {
            throw new InvalidOperationException("The Covenant Format-range identity evidence did not match its checked size.");
        }

        CovenantDigest hash = new(SHA256.HashData(evidence));

        evidence.Clear();
        return hash;
    }

    private static CovenantCompiledContent[] CompilePublishedVectors(
        CovenantCompiler compiler,
        out bool succeeded)
    {
        const string responseStyleAuthored = "  concise\r\nand\tclear  ";
        const string namesExampleAuthored = "Cafe\u0301";
        const string formatExampleAuthored = "Use \"A\"\nthen \\path and ``` marker";
        CovenantCompiledContent responseStyle = compiler.Compile("response.style", responseStyleAuthored);
        CovenantCompiledContent namesExample = compiler.Compile("names.example", namesExampleAuthored);
        CovenantCompiledContent formatExample = compiler.Compile("format.example", formatExampleAuthored);

        succeeded = Matches(
            responseStyle,
            responseStyleAuthored,
            "- response.style: \"concise and clear\"\n",
            3,
            "B5C835F676515711F21CA61CF53A9FDEABD16057BA2938B9E96930B0A984BB26",
            "E645D901DB511E428E00E1EC2E2F90F218B522FDBB1E3AEECF49BDCC43ED47BC");
        succeeded &= Matches(
            namesExample,
            namesExampleAuthored,
            "- names.example: \"Caf\u00e9\"\n",
            3,
            "A5B210DB411E4FE76BEC7C365E5E04F9738682ED5FC62DF64A80F5E9B56777AE",
            "23F6A8DB2CAB8CC51372551105FCED1FCF3B6937A6F0F80B365E23537A90F7CE");
        succeeded &= Matches(
            formatExample,
            formatExampleAuthored,
            "- format.example: \"Use \\\"A\\\" then \\\\path and ``` marker\"\n",
            4,
            "18C4323CE6C127A15608B0C64CB86D7B8997A56A21D9A239A243913E84A16B8A",
            "0217680F78F5B6B2C2ECD2022A2D3D80149A12794E2EEF33B2A473E1CDE5DA2A");

        return [responseStyle, namesExample, formatExample];
    }

    private static int RunFormatScalarCases(
        CovenantCompiler compiler,
        ReadOnlySpan<ulong> formatRanges,
        ref bool succeeded)
    {
        int count = 0;

        foreach (ulong packed in formatRanges)
        {
            int start = checked((int)(packed >> 21));
            int end = checked((int)(packed & 0x1fffff));

            for (int scalar = start; scalar <= end; scalar++)
            {
                string authored = string.Concat("a", char.ConvertFromUtf32(scalar), "b");

                succeeded &= CompileRejects(compiler, "format.scalar", authored);
                count++;
            }
        }

        return count;
    }

    private static int RunFormatAdjacentCases(
        CovenantCompiler compiler,
        ReadOnlySpan<ulong> formatRanges,
        ref bool succeeded)
    {
        int count = 0;

        foreach (ulong packed in formatRanges)
        {
            int start = checked((int)(packed >> 21));
            int end = checked((int)(packed & 0x1fffff));
            string before = string.Concat("a", char.ConvertFromUtf32(start - 1), "b");
            string after = string.Concat("a", char.ConvertFromUtf32(end + 1), "b");

            succeeded &= CompileAccepts(compiler, "format.adjacent", before, out _);
            count++;
            succeeded &= CompileAccepts(compiler, "format.adjacent", after, out _);
            count++;
        }

        return count;
    }

    private static int RunProhibitedControlCases(CovenantCompiler compiler, ref bool succeeded)
    {
        int count = 0;

        for (int scalar = 0; scalar <= 0x1f; scalar++)
        {
            if (scalar is 0x09 or 0x0a or 0x0d)
            {
                continue;
            }

            string authored = string.Concat("a", char.ConvertFromUtf32(scalar), "b");

            succeeded &= CompileRejects(compiler, "control.scalar", authored);
            count++;
        }

        for (int scalar = 0x7f; scalar <= 0x9f; scalar++)
        {
            string authored = string.Concat("a", char.ConvertFromUtf32(scalar), "b");

            succeeded &= CompileRejects(compiler, "control.scalar", authored);
            count++;
        }

        return count;
    }

    private static int RunMalformedUtf16Cases(CovenantCompiler compiler, ref bool succeeded)
    {
        string[] malformed =
        [
            new('\ud800', 1),
            new('\udfff', 1),
            string.Concat("before", new string('\ud800', 1), "after"),
            string.Concat("before", new string('\udfff', 1), "after")
        ];

        foreach (string authored in malformed)
        {
            succeeded &= CompileRejects(compiler, "unicode.invalid", authored);
        }

        return malformed.Length;
    }

    private static (int Accepted, int Rejected) RunKeyCases(
        CovenantCompiler compiler,
        ref bool succeeded)
    {
        int accepted = 0;
        int rejected = 0;

        for (int value = 0; value <= 0x7f; value++)
        {
            string key = ((char)value).ToString();

            if (value is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                succeeded &= CompileAccepts(compiler, key, "content", out _);
                accepted++;
            }
            else
            {
                succeeded &= CompileRejects(compiler, key, "content");
                rejected++;
            }
        }

        for (int value = 0; value <= 0x7f; value++)
        {
            string key = string.Concat("a", (char)value);

            if (value is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-')
            {
                succeeded &= CompileAccepts(compiler, key, "content", out _);
                accepted++;
            }
            else
            {
                succeeded &= CompileRejects(compiler, key, "content");
                rejected++;
            }
        }

        succeeded &= CompileAccepts(compiler, "a" + new string('z', 127), "content", out _);
        accepted++;

        string[] otherRejected = ["", "\u00e9", "a\u00e9", new string('a', 129)];

        foreach (string key in otherRejected)
        {
            succeeded &= CompileRejects(compiler, key, "content");
            rejected++;
        }

        return (accepted, rejected);
    }

    private static int RunUtf8BoundaryCases(CovenantCompiler compiler, ref bool succeeded)
    {
        succeeded &= CompileHasAuthoredByteCount(compiler, "limit.ascii", new string('a', 2_048), 2_048);
        succeeded &= CompileRejects(compiler, "limit.ascii", new string('a', 2_049));
        succeeded &= CompileHasAuthoredByteCount(compiler, "limit.two", new string('\u00e9', 1_024), 2_048);
        succeeded &= CompileRejects(compiler, "limit.two", new string('\u00e9', 1_025));
        succeeded &= CompileHasAuthoredByteCount(
            compiler,
            "limit.three",
            new string('\u20ac', 682) + "aa",
            2_048);
        succeeded &= CompileRejects(compiler, "limit.three", new string('\u20ac', 682) + "aaa");
        succeeded &= CompileHasAuthoredByteCount(compiler, "limit.four", Repeat("\U0001f600", 512), 2_048);
        succeeded &= CompileRejects(compiler, "limit.four", Repeat("\U0001f600", 513));

        return 8;
    }

    private static int RunWhitespaceCases(CovenantCompiler compiler, ref bool succeeded)
    {
        ReadOnlySpan<int> whitespaceScalars =
        [
            0x0009, 0x000a, 0x000d, 0x0020, 0x00a0, 0x1680,
            0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005,
            0x2006, 0x2007, 0x2008, 0x2009, 0x200a, 0x2028,
            0x2029, 0x202f, 0x205f, 0x3000
        ];
        StringBuilder authored = new("first");
        StringBuilder whitespaceOnly = new();

        foreach (int scalar in whitespaceScalars)
        {
            string value = char.ConvertFromUtf32(scalar);

            authored.Append(value);
            whitespaceOnly.Append(value);
        }

        authored.Append("second");
        succeeded &= CompileAccepts(compiler, "whitespace.example", authored.ToString(), out CovenantCompiledContent? compiled);
        succeeded &= string.Equals(
            compiled?.Fragment,
            "- whitespace.example: \"first second\"\n",
            StringComparison.Ordinal);
        succeeded &= CompileRejects(compiler, "whitespace.empty", whitespaceOnly.ToString());

        return whitespaceScalars.Length;
    }

    private static int RunRendererCases(CovenantCompiler compiler, ref bool succeeded)
    {
        ReadOnlySpan<string> contents = ["no ticks", "one ` tick", "two `` ticks", "three ``` ticks", "seven ``````` ticks"];
        ReadOnlySpan<int> fenceLengths = [3, 3, 3, 4, 8];

        for (int index = 0; index < contents.Length; index++)
        {
            succeeded &= CompileAccepts(compiler, "fence.example", contents[index], out CovenantCompiledContent? compiled);
            succeeded &= compiled?.RequiredFenceLength == fenceLengths[index];
        }

        succeeded &= CompileAccepts(
            compiler,
            "format.example",
            "Use \"A\"\r\nthen \\path",
            out CovenantCompiledContent? escaped);
        succeeded &= string.Equals(
            escaped?.Fragment,
            "- format.example: \"Use \\\"A\\\" then \\\\path\"\n",
            StringComparison.Ordinal);
        CovenantCompiledContent shortFence = compiler.Compile("alpha", "one ` tick");
        CovenantCompiledContent longFence = compiler.Compile("beta", "four ```` ticks");
        string section = compiler.RenderProposedSection([shortFence, longFence]);

        succeeded &= string.Equals(
            section,
            "`````text\n- alpha: \"one ` tick\"\n- beta: \"four ```` ticks\"\n`````\n",
            StringComparison.Ordinal);

        return 7;
    }

    private static bool CompileAccepts(
        CovenantCompiler compiler,
        string key,
        string authored,
        out CovenantCompiledContent? compiled)
    {
        try
        {
            compiled = compiler.Compile(key, authored);
            return true;
        }
        catch (ArgumentException)
        {
            compiled = null;
            return false;
        }
    }

    private static bool CompileRejects(CovenantCompiler compiler, string key, string authored)
    {
        try
        {
            compiler.Compile(key, authored);
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static bool CompileHasAuthoredByteCount(
        CovenantCompiler compiler,
        string key,
        string authored,
        int expectedByteCount) =>
        CompileAccepts(compiler, key, authored, out CovenantCompiledContent? compiled)
        && compiled is not null
        && compiled.AuthoredUtf8ByteCount == expectedByteCount;

    private static bool MatchesNormalization(string expected, string input)
    {
        try
        {
            return string.Equals(
                expected,
                CovenantUnicodePolicyV1.NormalizeToNfc(input),
                StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string Repeat(string value, int count)
    {
        char[] characters = new char[checked(value.Length * count)];

        try
        {
            for (int index = 0; index < count; index++)
            {
                value.AsSpan().CopyTo(characters.AsSpan(index * value.Length));
            }

            return new string(characters);
        }
        finally
        {
            characters.AsSpan().Clear();
        }
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> corpus, ref int offset)
    {
        EnsureRemaining(corpus, offset, sizeof(uint));
        uint value = BinaryPrimitives.ReadUInt32BigEndian(corpus[offset..]);
        offset = checked(offset + sizeof(uint));

        return value;
    }

    private static string ReadUtf8(ReadOnlySpan<byte> corpus, ref int offset)
    {
        EnsureRemaining(corpus, offset, sizeof(ushort));
        int byteCount = BinaryPrimitives.ReadUInt16BigEndian(corpus[offset..]);
        offset = checked(offset + sizeof(ushort));

        if (byteCount > CovenantLimits.MaxAuthoredContentBytes)
        {
            throw new ArgumentException("A Covenant compiler corpus field exceeds the authored-content limit.", nameof(corpus));
        }

        EnsureRemaining(corpus, offset, byteCount);
        string value;

        try
        {
            value = StrictUtf8.GetString(corpus.Slice(offset, byteCount));
        }
        catch (DecoderFallbackException exception)
        {
            throw new ArgumentException("The Covenant compiler corpus contains malformed UTF-8.", nameof(corpus), exception);
        }

        offset = checked(offset + byteCount);
        return value;
    }

    private static void EnsureRemaining(ReadOnlySpan<byte> corpus, int offset, int byteCount)
    {
        if (offset < 0 || byteCount < 0 || offset > corpus.Length - byteCount)
        {
            throw new ArgumentException("The Covenant compiler corpus is truncated.", nameof(corpus));
        }
    }

    private static CovenantDigest ComputeCompleteAggregate(
        ReadOnlySpan<byte> corpusHash,
        CovenantDigest formatRangeIdentityHash,
        int normalizationCaseCount,
        int normalizationAssertionCount,
        int formatScalarCount,
        int formatAdjacentScalarCount,
        int prohibitedControlCount,
        int malformedUtf16CaseCount,
        int acceptedKeyCaseCount,
        int rejectedKeyCaseCount,
        int utf8BoundaryCaseCount,
        int whitespaceScalarCount,
        int rendererCaseCount,
        IReadOnlyList<CovenantCompiledContent> vectors,
        bool succeeded)
    {
        ReadOnlySpan<byte> domain = "Arcanum.Covenant.CompilerCorpus.v1\0"u8;
        Span<byte> evidence = stackalloc byte[340];
        int offset = 0;

        domain.CopyTo(evidence);
        offset += domain.Length;
        corpusHash.CopyTo(evidence[offset..]);
        offset += CovenantLimits.DigestBytes;
        byte[] formatRangeIdentityHashBytes = formatRangeIdentityHash.Bytes;

        formatRangeIdentityHashBytes.CopyTo(evidence[offset..]);
        offset += formatRangeIdentityHashBytes.Length;
        formatRangeIdentityHashBytes.AsSpan().Clear();
        WriteUInt32(evidence, ref offset, normalizationCaseCount);
        WriteUInt32(evidence, ref offset, normalizationAssertionCount);
        WriteUInt32(evidence, ref offset, formatScalarCount);
        WriteUInt32(evidence, ref offset, formatAdjacentScalarCount);
        WriteUInt32(evidence, ref offset, prohibitedControlCount);
        WriteUInt32(evidence, ref offset, malformedUtf16CaseCount);
        WriteUInt32(evidence, ref offset, acceptedKeyCaseCount);
        WriteUInt32(evidence, ref offset, rejectedKeyCaseCount);
        WriteUInt32(evidence, ref offset, utf8BoundaryCaseCount);
        WriteUInt32(evidence, ref offset, whitespaceScalarCount);
        WriteUInt32(evidence, ref offset, rendererCaseCount);
        WriteUInt32(evidence, ref offset, vectors.Count);

        foreach (CovenantCompiledContent vector in vectors)
        {
            byte[] authoredHash = vector.AuthoredHash.Bytes;
            byte[] fragmentHash = vector.FragmentHash.Bytes;

            authoredHash.CopyTo(evidence[offset..]);
            offset += authoredHash.Length;
            fragmentHash.CopyTo(evidence[offset..]);
            offset += fragmentHash.Length;
            authoredHash.AsSpan().Clear();
            fragmentHash.AsSpan().Clear();
        }

        evidence[offset] = succeeded ? (byte)1 : (byte)0;
        offset++;

        if (offset != evidence.Length)
        {
            throw new InvalidOperationException("The complete Covenant compiler corpus evidence did not match its checked size.");
        }

        CovenantDigest aggregate = new(SHA256.HashData(evidence));

        evidence.Clear();
        return aggregate;
    }

    private static void WriteUInt32(Span<byte> destination, ref int offset, int value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination[offset..], checked((uint)value));
        offset = checked(offset + sizeof(uint));
    }

    private static bool Matches(
        CovenantCompiledContent compiled,
        string authored,
        string fragment,
        int fenceLength,
        string authoredHash,
        string fragmentHash) =>
        string.Equals(compiled.AuthoredContent, authored, StringComparison.Ordinal)
        && compiled.AuthoredUtf8.AsSpan().SequenceEqual(StrictUtf8.GetBytes(authored))
        && string.Equals(compiled.Fragment, fragment, StringComparison.Ordinal)
        && compiled.FragmentUtf8.AsSpan().SequenceEqual(StrictUtf8.GetBytes(fragment))
        && compiled.RequiredFenceLength == fenceLength
        && string.Equals(compiled.AuthoredHash.ToString(), authoredHash, StringComparison.Ordinal)
        && string.Equals(compiled.FragmentHash.ToString(), fragmentHash, StringComparison.Ordinal)
        && compiled.CompilerPolicyVersion == CovenantCompiler.CompilerPolicyVersion
        && compiled.RendererPolicyVersion == CovenantCompiler.RendererPolicyVersion;
}
