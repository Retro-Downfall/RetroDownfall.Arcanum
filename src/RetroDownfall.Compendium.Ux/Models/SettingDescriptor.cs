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
    string? Group = null);
