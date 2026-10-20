using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.MemoryDelegation;
using RetroDownfall.Arcanum.Core.Primitives;

namespace RetroDownfall.Arcanum.Api.Intelligence.MemoryDelegation;

/// <summary>Non-reading output guard. It conveys no selected content or child read capability.</summary>
internal sealed class MemoryDelegationProtection(
    ProviderCallSensitivity sensitivity,
    CancellationToken revocation,
    Func<CancellationToken, Task<Result<MemoryDelegationEffect>>> admit) : IProtectedProviderCallBinding
{
    internal ProviderCallSensitivity Sensitivity { get; } = sensitivity;

    internal MemoryDelegationUsageCommit? UsageCommit { get; private init; }

    internal MemoryDelegationProtection WithUsageCommit(MemoryDelegationUsageCommit usage) =>
        new(Sensitivity, Revocation, admit) { UsageCommit = usage };

    internal CancellationToken Revocation { get; } = revocation;

    internal Task<Result<MemoryDelegationEffect>> AdmitAsync(CancellationToken ct) => admit(ct);
}

/// <summary>Host-created result consumed before arbitrary tool result stringification.</summary>
internal sealed class MemoryDelegationToolResult(string text, ProviderCallSensitivity sensitivity)
{
    internal string Text { get; } = text;

    internal ProviderCallSensitivity Sensitivity { get; } = sensitivity;

    public override string ToString() => Text;
}
