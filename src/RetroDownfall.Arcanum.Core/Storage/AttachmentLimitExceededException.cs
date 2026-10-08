namespace RetroDownfall.Arcanum.Core.Storage;

/// <summary>
/// The session-attachment store refused to persist because a storage limit would be crossed: the session's
/// physical byte budget, or a logical key's version counter.
/// </summary>
/// <remarks>
/// A dedicated type so a caller can tell a limit from any other <see cref="InvalidOperationException"/>
/// the store raises (a failed database write, a resolver whose bytes disagree with its own hash), none of
/// which the caller can fix by sending less. It derives from <see cref="InvalidOperationException"/> so the
/// callers that catch the base type, which is everything that predates it, keep working unchanged.
/// </remarks>
public sealed class AttachmentLimitExceededException(string message) : InvalidOperationException(message);
