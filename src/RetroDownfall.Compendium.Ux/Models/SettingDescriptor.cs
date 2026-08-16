namespace RetroDownfall.Compendium.Ux.Models;

public enum SettingKind
{

    String,

    Int,

    Long,

    Float,

    Bool,

    Enum,

    StringArray,

    Path,

    Secret,

    Color,

    Dictionary,

}

/// <summary>
/// A resolver the generic editor runs to turn one setting into explicit help actions.
/// </summary>
/// <remarks>
/// Closed and single-membered on purpose. A descriptor cannot carry a URI of its own, because a URI
/// in a descriptor table is a link nobody re-evaluates: the correct retention page depends on which
/// providers this installation actually has configured, which is known at render time and not at
/// declaration time (§10.18).
/// </remarks>
public enum SettingHelpRoute
{

    /// <summary>
    /// Resolve retention and data-use help against the currently configured providers.
    /// </summary>
    ConfiguredProviderRetention,

}

public sealed record SettingDescriptor(
    string Key,
    ConfigSection Section,
    string Label,
    string Description,
    SettingKind Kind,
    double Min = 0,
    double Max = 0,
    double Increment = 1,
    Type? EnumType = null,
    string Placeholder = "",
    string? ClampName = null,
    string? Group = null,
    bool AllowUnset = false,
    SettingHelpRoute? HelpRoute = null);
