using System.Diagnostics.CodeAnalysis;

namespace RetroDownfall.Arcanum.Core;

/// <summary>
/// Compile-time build metadata (AOT-safe; no reflection). Version is generated at build.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: build metadata stub, version generated at compile time
public static partial class ArcanumBuildInfo;
