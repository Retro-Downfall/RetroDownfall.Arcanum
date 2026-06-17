namespace RetroDownfall.Arcanum.Core.Events;

/// <summary>
/// Optional marker base for domain events published on <see cref="IEventBus"/>.
/// Concrete subtypes are registered on <c>ArcanumJsonContext</c>; this type is not.
/// </summary>
public abstract record ArcanumEvent(DateTimeOffset Timestamp);
