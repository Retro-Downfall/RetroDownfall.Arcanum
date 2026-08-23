using System.Text.Json.Serialization;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Core.Tower;

[JsonConverter(typeof(StringOnlyJsonStringEnumConverter<SessionCampaignBindingKind>))]
public enum SessionCampaignBindingKind : byte
{
    GlobalOnly = 1,
    Campaign = 2,
    LegacyUnresolved = 3
}

public readonly record struct SessionCampaignBinding
{
    private readonly bool _isInitialized;

    private readonly SessionCampaignBindingKind _kind;

    private readonly Guid? _campaignId;

    private SessionCampaignBinding(SessionCampaignBindingKind kind, Guid? campaignId)
    {
        _isInitialized = true;
        _kind = kind;
        _campaignId = campaignId;
    }

    public SessionCampaignBindingKind Kind
    {
        get
        {
            EnsureInitialized();

            return _kind;
        }
    }

    public Guid? CampaignId
    {
        get
        {
            EnsureInitialized();

            return _campaignId;
        }
    }

    public static SessionCampaignBinding GlobalOnly =>
        new(SessionCampaignBindingKind.GlobalOnly, null);

    public static SessionCampaignBinding LegacyUnresolved =>
        new(SessionCampaignBindingKind.LegacyUnresolved, null);

    public static SessionCampaignBinding ForCampaign(Guid campaignId) =>
        Create(SessionCampaignBindingKind.Campaign, campaignId);

    public static SessionCampaignBinding Create(
        SessionCampaignBindingKind kind,
        Guid? campaignId)
    {
        return kind switch
        {
            SessionCampaignBindingKind.GlobalOnly when campaignId is null => GlobalOnly,
            SessionCampaignBindingKind.Campaign when campaignId is { } value && value != Guid.Empty => new(kind, value),
            SessionCampaignBindingKind.LegacyUnresolved when campaignId is null => LegacyUnresolved,
            _ => throw new ArgumentException("The Session Campaign binding discriminant does not match its Campaign identity.", nameof(campaignId))
        };
    }

    private void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException("The Session Campaign binding is uninitialized.");
        }
    }
}
