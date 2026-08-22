using System.Text;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Core.Pattern;

public static class PatternSnapshotLimits
{

    public static int MaxThreads { get; } =
        ArcanumSettingClamps.MaxTableOfContentsLines(
            ArcanumRuntimeDefaults.Perception.MaxTableOfContentsLines);

    public const int MaxRootPathCharacters = 32 * 1024;

    public const int MaxThreadCharacters = MaxRootPathCharacters + 64;

    public const int MaxAggregateUtf8Bytes = 2 * 1024 * 1024;

}

public static class PatternSnapshotValidator
{

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static Result Validate(PatternSnapshot? snapshot)
    {

        if (snapshot is null)
        {

            return Invalid("A PatternSnapshot request body is required.");

        }

        if (!Enum.IsDefined(snapshot.Domain))
        {

            return Invalid("PatternSnapshot.Domain must be a defined DomainType value.");

        }

        Result rootValidation = ValidateRoot(snapshot.RootPath, out int aggregateUtf8Bytes);

        if (rootValidation.IsFailure)
        {

            return rootValidation;

        }

        string[] threads = snapshot.Threads;

        if (threads.Length > PatternSnapshotLimits.MaxThreads)
        {

            return Invalid(
                $"PatternSnapshot.Threads cannot contain more than {PatternSnapshotLimits.MaxThreads} entries.");

        }

        HashSet<string> identities = new(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < threads.Length; index++)
        {

            string? thread = threads[index];

            if (string.IsNullOrWhiteSpace(thread))
            {

                return Invalid($"PatternSnapshot.Threads[{index}] must be nonblank.");

            }

            if (thread.Length > PatternSnapshotLimits.MaxThreadCharacters)
            {

                return Invalid(
                    $"PatternSnapshot.Threads[{index}] cannot exceed "
                    + $"{PatternSnapshotLimits.MaxThreadCharacters} characters.");

            }

            if (!TryGetStrictUtf8ByteCount(thread, out int threadUtf8Bytes))
            {

                return Invalid($"PatternSnapshot.Threads[{index}] must contain valid Unicode scalar values.");

            }

            aggregateUtf8Bytes += threadUtf8Bytes;

            if (aggregateUtf8Bytes > PatternSnapshotLimits.MaxAggregateUtf8Bytes)
            {

                return Invalid(
                    $"PatternSnapshot text cannot exceed "
                    + $"{PatternSnapshotLimits.MaxAggregateUtf8Bytes} strict UTF-8 bytes in aggregate.");

            }

            if (!identities.Add(GetEyeIdentity(thread)))
            {

                return Invalid("PatternSnapshot.Threads cannot contain duplicate Eye identities.");

            }

        }

        return Result.Success();

    }

    private static Result ValidateRoot(string? rootPath, out int utf8Bytes)
    {

        utf8Bytes = 0;

        if (string.IsNullOrWhiteSpace(rootPath))
        {

            return Invalid("PatternSnapshot.RootPath must be nonblank.");

        }

        if (rootPath.Length > PatternSnapshotLimits.MaxRootPathCharacters)
        {

            return Invalid(
                $"PatternSnapshot.RootPath cannot exceed "
                + $"{PatternSnapshotLimits.MaxRootPathCharacters} characters.");

        }

        if (!TryGetStrictUtf8ByteCount(rootPath, out utf8Bytes))
        {

            return Invalid("PatternSnapshot.RootPath must contain valid Unicode scalar values.");

        }

        if (!Path.IsPathFullyQualified(rootPath))
        {

            return Invalid("PatternSnapshot.RootPath must be a canonical fully-qualified path.");

        }

        string canonical;

        try
        {

            canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));

        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {

            return Invalid("PatternSnapshot.RootPath must be a canonical fully-qualified path.");

        }

        return string.Equals(rootPath, canonical, StringComparison.Ordinal)
            ? Result.Success()
            : Invalid("PatternSnapshot.RootPath must use its canonical lexical form.");

    }

    private static bool TryGetStrictUtf8ByteCount(string value, out int byteCount)
    {

        try
        {

            byteCount = StrictUtf8.GetByteCount(value);

            return true;

        }
        catch (EncoderFallbackException)
        {

            byteCount = 0;

            return false;

        }

    }

    private static string GetEyeIdentity(string thread)
    {

        int separator = thread.IndexOf(':');

        return separator < 0
            ? thread
            : thread[(separator + 1)..].TrimStart();

    }

    private static Result Invalid(string message) =>
        Result.Failure(new Error(ErrorCodes.Perception.InvalidSnapshot, message));

}
