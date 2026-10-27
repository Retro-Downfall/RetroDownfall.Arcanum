using System.Globalization;

namespace RetroDownfall.Arcanum.Core.Covenant;

/// <summary>A closed compilation cause, carried inside the existing ArgumentException contract.</summary>
public sealed class CovenantCompilationFailure : Exception
{
    private CovenantCompilationFailure(CovenantCompilationFailureReason reason, int? scalar, int? position, Exception? cause)
        : base("A declared Covenant compilation policy refused the input.", cause)
    {
        Reason = reason;

        Scalar = scalar;

        Position = position;
    }

    public CovenantCompilationFailureReason Reason { get; }

    public int? Scalar { get; }

    /// <summary>The one-based Unicode-scalar position in authored input, when known.</summary>
    public int? Position { get; }

    public const string KeyPolicy = "The key must match [a-z0-9][a-z0-9._-]{0,127}.";

    internal static ArgumentException Argument(CovenantCompilationFailureReason reason, string parameter,
        int? scalar = null, int? position = null, Exception? cause = null) =>
        new(Describe(reason, CovenantCompilationField.Content, scalar, position), parameter,
            new CovenantCompilationFailure(reason, scalar, position, cause));

    public static CovenantCompilationFailureReason ReasonOf(ArgumentException failure) =>
        failure.InnerException is CovenantCompilationFailure known ? known.Reason : CovenantCompilationFailureReason.Unknown;

    /// <summary>Describes declared policy and safe scalar metadata without inspecting exception text.</summary>
    public static string Describe(ArgumentException failure, CovenantCompilationField field) =>
        failure.InnerException is CovenantCompilationFailure known
            ? Describe(known.Reason, field, known.Scalar, known.Position)
            : Describe(CovenantCompilationFailureReason.Unknown, field, null, null);

    private static string Describe(CovenantCompilationFailureReason reason, CovenantCompilationField field,
        int? scalar, int? position)
    {
        string name = field == CovenantCompilationField.ReplacementContent ? "replacement content (replacementContent)" : "content";

        string location = scalar is { } value && position is > 0
            ? " U+" + value.ToString("X4", CultureInfo.InvariantCulture) + " at Unicode scalar position "
                + position.Value.ToString(CultureInfo.InvariantCulture)
            : string.Empty;

        return reason switch
        {
            CovenantCompilationFailureReason.KeyGrammar => KeyPolicy,
            CovenantCompilationFailureReason.InvalidUnicode => $"The {name} must contain valid Unicode scalar values. Correct invalid UTF-16 input before resending it.",
            CovenantCompilationFailureReason.ByteLimit => $"The {name} must contain between 1 and 2048 strict UTF-8 bytes.",
            CovenantCompilationFailureReason.WhitespaceOnly => $"The {name} cannot be empty or whitespace-only.",
            CovenantCompilationFailureReason.ControlScalar => $"The {name} contains a prohibited control{location}. Remove that character; tab, LF and CR are permitted in ordinary content.",
            CovenantCompilationFailureReason.FormatScalar => $"The {name} contains a prohibited Unicode 17 Format scalar{location}. Remove that character before resending it.",
            CovenantCompilationFailureReason.OperationalDefaultKey => "This operational default key is unsupported. Use defaults.inference.temperature or defaults.inference.top-p.",
            CovenantCompilationFailureReason.OperationalDefaultValue => $"The {name} for an operational default must be a finite unsigned decimal with no whitespace, including no trailing newline. Temperature permits 0 through 2; top-p permits 0 through 1.",
            _ => $"The {name} cannot be compiled under the Covenant text policy. Check its strict UTF-8 size, Unicode scalars and prohibited Format/control characters.",
        };
    }
}

/// <summary>The declared input policy that refused compilation, never an authored string.</summary>
public enum CovenantCompilationFailureReason : byte
{
    Unknown,

    KeyGrammar,

    InvalidUnicode,

    ByteLimit,

    WhitespaceOnly,

    ControlScalar,

    FormatScalar,

    OperationalDefaultKey,

    OperationalDefaultValue,
}

/// <summary>The fixed wire field named by a compilation refusal.</summary>
public enum CovenantCompilationField : byte
{
    Content,

    ReplacementContent,
}
