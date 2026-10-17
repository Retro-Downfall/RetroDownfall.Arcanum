using Microsoft.CodeAnalysis;

namespace RetroDownfall.Arcanum.Tests.Support;

internal static partial class HostedGrimoireProducerInventory
{
    private sealed record ReviewedNativeImport(
        string DeclaringType,
        string Member,
        string Library,
        string EntryPoint,
        SpecialType Parameter,
        SpecialType Result);

    // The shipping CLI's two process-descriptor P/Invokes. Neither touches a Grimoire connection, a provider,
    // or a managed file path, so both are reviewed non-producers. A name-keyed entry would also accept a
    // lookalike, so the pin is the declaring assembly and type, the member, the DllImport library and entry
    // point, SetLastError, accessibility and the exact one-parameter signature.
    private static readonly ReviewedNativeImport[] ReviewedNativeImports =
    [
        new(
            "RetroDownfall.Arcanum.Cli.Services.FileDescriptorBackupPassphraseReader",
            "DuplicateFileDescriptor",
            "libc",
            "dup",
            SpecialType.System_Int32,
            SpecialType.System_Int32),
        new(
            "RetroDownfall.Arcanum.Cli.Services.FileDescriptorBackupPassphraseReader",
            "GetOperatingSystemHandle",
            "msvcrt.dll",
            "_get_osfhandle",
            SpecialType.System_Int32,
            SpecialType.System_IntPtr),
    ];

    private static bool IsReviewedExactNativeImport(IMethodSymbol method)
    {
        if (method is not
            {
                MethodKind: MethodKind.Ordinary,
                IsExtern: true,
                IsStatic: true,
                IsGenericMethod: false,
                DeclaredAccessibility: Accessibility.Private,
                RefKind: RefKind.None,
                ContainingAssembly.Name: "RetroDownfall.Arcanum.Cli",
                Parameters: [{ RefKind: RefKind.None }],
            }
            || method.GetDllImportData() is not { SetLastError: true } import)
        {
            return false;
        }

        return ReviewedNativeImports.Any(entry =>
            method.Name == entry.Member
            && TypeKey(method.ContainingType) == entry.DeclaringType
            && import.ModuleName == entry.Library
            && import.EntryPointName == entry.EntryPoint
            && method.Parameters[0].Type.SpecialType == entry.Parameter
            && method.ReturnType.SpecialType == entry.Result);
    }
}
