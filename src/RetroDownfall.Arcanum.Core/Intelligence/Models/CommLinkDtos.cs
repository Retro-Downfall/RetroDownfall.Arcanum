using RetroDownfall.Arcanum.Core.CommLink;

namespace RetroDownfall.Arcanum.Core.Intelligence.Models;

public sealed record CommLinkMessageRequestDto(
    string Title,
    string Body,
    CommLinkSeverity Severity,
    string Source);
