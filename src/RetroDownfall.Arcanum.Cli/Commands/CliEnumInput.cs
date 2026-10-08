namespace RetroDownfall.Arcanum.Cli.Commands;

/// <summary>
/// Parses an option value that names an enumeration member.
/// </summary>
/// <remarks>
/// <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> alone is a cast, not a vocabulary: it
/// accepts numeric ordinals (<c>1</c>), ordinals that name no member (<c>99</c>) and comma-joined
/// lists (<c>session,saga</c>), each of which would send the host a value the operator never named and
/// the command's own message never advertised. Requiring letters only and then a declared member is
/// what makes the option's "must be one of" message true.
/// </remarks>
internal static class CliEnumInput
{
    /// <summary>
    /// Whether <paramref name="value"/> is the name, in any case, of a declared member of <typeparamref name="TEnum"/>.
    /// </summary>
    public static bool TryParseName<TEnum>(string? value, out TEnum result)
        where TEnum : struct, Enum
    {
        result = default;

        string? trimmed = value?.Trim();

        return !string.IsNullOrEmpty(trimmed)
            && trimmed.All(char.IsLetter)
            && Enum.TryParse(trimmed, ignoreCase: true, out result)
            && Enum.IsDefined(result);
    }
}
