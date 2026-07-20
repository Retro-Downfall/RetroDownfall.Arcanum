using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Storage;

namespace RetroDownfall.Arcanum.Api.Intelligence;

/// <summary>
/// Host-side validation for <see cref="PingRequest.AttachmentReferences"/> before rehydrate/persist.
/// Returns <see langword="null"/> on success; otherwise a clear error message for the caller to wrap.
/// </summary>
public static class SessionAttachmentRequestValidator
{

    /// <summary>
    /// Validates attachment reference ids on <paramref name="request"/> against
    /// <paramref name="settings"/> and <paramref name="store"/>. No-op when references are null/empty.
    /// </summary>
    public static async Task<string?> ValidateAsync(
        PingRequest request,
        ISessionAttachmentStore store,
        AttachmentsSettings settings,
        CancellationToken cancellationToken = default)
    {

        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);

        List<Guid>? refs = request.AttachmentReferences;

        if (refs is null || refs.Count == 0)
        {
            return null;
        }

        if (!settings.Enabled)
        {
            return "Session attachments are disabled. Enable Arcanum:Attachments:Enabled to send attachment references.";
        }

        if (request.SessionId is null)
        {
            return "AttachmentReferences require a SessionId.";
        }

        int maxRefs = ArcanumSettingClamps.AttachmentsMaxReferencesPerTurn(settings.MaxReferencesPerTurn);

        if (refs.Count > maxRefs)
        {
            return $"At most {maxRefs} attachment references are allowed per request.";
        }

        try
        {
            await store
                .ValidateReferencesAsync(request.SessionId.Value, refs, maxRefs, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }

        return null;

    }

}
