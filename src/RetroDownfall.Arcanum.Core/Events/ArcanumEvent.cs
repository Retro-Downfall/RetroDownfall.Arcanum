namespace RetroDownfall.Arcanum.Core.Events;

/// <summary>
/// Optional marker base for domain events published on <see cref="IEventBus"/>.
/// Concrete subtypes are registered for SSE serialization on <c>ArcanumJsonContext</c> in the
/// <c>RetroDownfall.Arcanum.Api</c> project (<c>Api/Serialization/ArcanumJsonContext.cs</c>), not in
/// Core; this base type itself is not registered.
/// </summary>
public abstract record ArcanumEvent(DateTimeOffset Timestamp);
