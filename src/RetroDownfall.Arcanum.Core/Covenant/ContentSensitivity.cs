using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Covenant;

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<ContentSensitivity>))]
public enum ContentSensitivity : byte
{
    None = 0,
    CovenantDerived = 1
}

public static class ContentSensitivityAlgebra
{
    public static ContentSensitivity Maximum(ContentSensitivity left, ContentSensitivity right)
    {
        Validate(left, nameof(left));
        Validate(right, nameof(right));

        return (ContentSensitivity)Math.Max((byte)left, (byte)right);
    }

    internal static void Validate(ContentSensitivity value, string parameterName)
    {
        if (value is not ContentSensitivity.None and not ContentSensitivity.CovenantDerived)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    internal static void Validate(ContentSensitivity value, GenerationProvenance provenance, string parameterName)
    {
        Validate(value, nameof(value));
        ArgumentNullException.ThrowIfNull(provenance);

        if (value == ContentSensitivity.None && !provenance.IsEmpty)
        {
            throw new ArgumentException("Nonsensitive content requires empty exact provenance.", parameterName);
        }

        if (value == ContentSensitivity.CovenantDerived && provenance.IsEmpty)
        {
            throw new ArgumentException("Covenant-derived content requires generation provenance.", parameterName);
        }
    }
}
