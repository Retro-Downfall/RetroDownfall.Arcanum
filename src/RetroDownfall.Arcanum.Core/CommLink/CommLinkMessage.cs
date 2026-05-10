namespace RetroDownfall.Arcanum.Core.CommLink;

public readonly record struct CommLinkMessage(string Title, string Body, CommLinkSeverity Severity, string Source);
