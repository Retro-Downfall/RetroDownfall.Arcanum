using RetroDownfall.Arcanum.Core.Pattern;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Tests.Pattern;

public sealed class PatternSnapshotValidatorTests
{

    [Theory]
    [InlineData(DomainType.SoftwareEngineering)]
    [InlineData(DomainType.Administration)]
    [InlineData(DomainType.Research)]
    [InlineData(DomainType.Unknown)]
    public void Validate_AcceptsEveryDefinedDomainAndAnEmptyProjection(DomainType domain)
    {

        PatternSnapshot snapshot = new(domain, CanonicalRoot(), []);

        Result result = PatternSnapshotValidator.Validate(snapshot);

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void Validate_RejectsAnUndefinedDomain()
    {

        PatternSnapshot snapshot = new((DomainType)99, CanonicalRoot(), []);

        Result result = PatternSnapshotValidator.Validate(snapshot);

        AssertInvalidSnapshot(result);

    }

    [Theory]
    [MemberData(nameof(InvalidRoots))]
    public void Validate_RejectsANonCanonicalRoot(string root)
    {

        PatternSnapshot snapshot = new(DomainType.Unknown, root, []);

        Result result = PatternSnapshotValidator.Validate(snapshot);

        AssertInvalidSnapshot(result);

    }

    [Fact]
    public void Validate_RejectsAnInvalidUnicodeRoot()
    {

        PatternSnapshot snapshot = new(
            DomainType.Unknown,
            CanonicalRoot() + '\ud800',
            []);

        Result result = PatternSnapshotValidator.Validate(snapshot);

        AssertInvalidSnapshot(result);

    }

    [Fact]
    public void Validate_AcceptsExactlyTwentyThreads()
    {

        string[] threads = Enumerable.Range(0, 20)
            .Select(static index => $"File: item-{index:D2}")
            .ToArray();

        Result result = PatternSnapshotValidator.Validate(
            new PatternSnapshot(DomainType.Unknown, CanonicalRoot(), threads));

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void Validate_RejectsTwentyOneThreads()
    {

        string[] threads = Enumerable.Range(0, 21)
            .Select(static index => $"File: item-{index:D2}")
            .ToArray();

        Result result = PatternSnapshotValidator.Validate(
            new PatternSnapshot(DomainType.Unknown, CanonicalRoot(), threads));

        AssertInvalidSnapshot(result);

    }

    [Theory]
    [MemberData(nameof(InvalidThreads))]
    public void Validate_RejectsANullOrBlankThread(string? thread)
    {

        Result result = PatternSnapshotValidator.Validate(
            new PatternSnapshot(DomainType.Unknown, CanonicalRoot(), [thread!]));

        AssertInvalidSnapshot(result);

    }

    [Fact]
    public void Validate_RejectsAnInvalidUnicodeThread()
    {

        PatternSnapshot snapshot = new(
            DomainType.Unknown,
            CanonicalRoot(),
            ["File: invalid\ud800"]);

        Result result = PatternSnapshotValidator.Validate(snapshot);

        AssertInvalidSnapshot(result);

    }

    [Fact]
    public void Validate_AcceptsAThreadAtTheCharacterBoundary()
    {

        string thread = "File: " + new string('a', 32_826);

        Assert.Equal(32_832, thread.Length);

        Result result = PatternSnapshotValidator.Validate(
            new PatternSnapshot(DomainType.Unknown, CanonicalRoot(), [thread]));

        Assert.True(result.IsSuccess);

    }

    [Fact]
    public void Validate_RejectsAThreadPastTheCharacterBoundary()
    {

        string thread = "File: " + new string('a', 32_827);

        Assert.Equal(32_833, thread.Length);

        Result result = PatternSnapshotValidator.Validate(
            new PatternSnapshot(DomainType.Unknown, CanonicalRoot(), [thread]));

        AssertInvalidSnapshot(result);

    }

    [Fact]
    public void Validate_RejectsDuplicateEyeIdentitiesIgnoringLabelAndCase()
    {

        PatternSnapshot snapshot = new(
            DomainType.SoftwareEngineering,
            CanonicalRoot(),
            ["Project: src/App.csproj", "Document: SRC/app.CSPROJ"]);

        Result result = PatternSnapshotValidator.Validate(snapshot);

        AssertInvalidSnapshot(result);

    }

    [Fact]
    public void Validate_AcceptsTheStrictUtf8WorstCaseWithinAllOtherBounds()
    {

        string[] threads = Enumerable.Range(0, 20)
            .Select(index => $"File: {index:D2}" + new string('\u0800', 32_824))
            .ToArray();

        Assert.All(threads, static thread => Assert.Equal(32_832, thread.Length));

        Result result = PatternSnapshotValidator.Validate(
            new PatternSnapshot(DomainType.Unknown, CanonicalRoot(), threads));

        Assert.True(result.IsSuccess);

    }

    public static TheoryData<string> InvalidRoots()
    {

        string canonical = CanonicalRoot();

        string relativeWithDotSegment = Path.Combine("relative", ".", "workspace");

        return
        [
            null!,
            string.Empty,
            "   ",
            relativeWithDotSegment,
            Path.Combine(canonical, ".", "workspace"),
            canonical + Path.DirectorySeparatorChar,
            RootPrefix() + new string('a', 32_769),
        ];

    }

    public static TheoryData<string?> InvalidThreads() =>
    [
        null,
        string.Empty,
        "   ",
    ];

    private static string CanonicalRoot() =>
        Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(
                Path.Combine(Path.GetTempPath(), "arcanum-pattern-validator")));

    private static string RootPrefix()
    {

        string root = Path.GetPathRoot(CanonicalRoot())!;

        return root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

    }

    private static void AssertInvalidSnapshot(Result result)
    {

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Perception.InvalidSnapshot, result.Error.Code);

    }

}
