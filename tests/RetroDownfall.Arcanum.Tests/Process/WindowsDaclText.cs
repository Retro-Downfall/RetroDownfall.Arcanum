using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Text;

namespace RetroDownfall.Arcanum.Tests.Process;

/// <summary>
/// The DACL of a directory as text, for the tests that assert a root's access list was restored
/// exactly.
/// </summary>
/// <remarks>
/// The broker writes a root's DACL through <c>DirectorySecurity</c> and <c>SetAccessControl</c>, which
/// reach <c>SetSecurityInfo</c> with inheritance propagation. Propagation is the point: the grant is
/// inheritable and has to reach the children a root already holds. Windows marks any DACL written that
/// way <c>SE_DACL_AUTO_INHERITED</c>, which SDDL spells as the <c>AI</c> control flag, so a descriptor
/// read back after a grant and its removal is <c>D:AI(...)</c> where the original was <c>D:(...)</c>
/// with every ACE the same. The flag is bookkeeping for the inheritance engine and takes no part in an
/// access check, so it is not part of what "restored" means here; comparing it would fail a restore
/// that put every ACE back.
///
/// <para>Only that one flag is dropped. The protected flag <c>P</c>, the auto-inherit-request flag
/// <c>AR</c> and every ACE stay in the text, so a restore that lost protection or an entry still
/// differs.</para>
/// </remarks>
internal static class WindowsDaclText
{

    private const string DaclMarker = "D:";

    private const string AutoInherited = "AI";

    private const string AutoInheritRequested = "AR";

    private const char Protected = 'P';

    /// <summary>
    /// The DACL of <paramref name="path"/> as SDDL, comparable across a grant and its removal.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static string Of(string path) =>
        WithoutAutoInheritedFlag(
            new DirectoryInfo(path)
                .GetAccessControl(AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access));

    /// <summary>
    /// <paramref name="sddl"/> with the <c>AI</c> control flag removed from its <c>D:</c> component and
    /// nothing else changed.
    /// </summary>
    /// <remarks>
    /// Only the run of flags between <c>D:</c> and the first ACE is read, because <c>AI</c> is also
    /// text an ACE may legitimately contain.
    /// </remarks>
    internal static string WithoutAutoInheritedFlag(string sddl)
    {

        ArgumentNullException.ThrowIfNull(sddl);

        if (!sddl.StartsWith(DaclMarker, StringComparison.Ordinal))
        {

            throw new ArgumentException(
                $"Expected a DACL-only SDDL string beginning '{DaclMarker}', got '{sddl}'.",
                nameof(sddl));

        }

        int aclStart = sddl.IndexOf('(', DaclMarker.Length);

        string flags = aclStart < 0
            ? sddl[DaclMarker.Length..]
            : sddl[DaclMarker.Length..aclStart];

        StringBuilder kept = new(flags.Length);

        int index = 0;

        while (index < flags.Length)
        {

            if (flags[index] == Protected)
            {

                _ = kept.Append(Protected);

                index++;

                continue;

            }

            if (flags.AsSpan(index).StartsWith(AutoInherited, StringComparison.Ordinal))
            {

                index += AutoInherited.Length;

                continue;

            }

            if (flags.AsSpan(index).StartsWith(AutoInheritRequested, StringComparison.Ordinal))
            {

                _ = kept.Append(AutoInheritRequested);

                index += AutoInheritRequested.Length;

                continue;

            }

            // Anything else (NO_ACCESS_CONTROL, a flag this reader does not know) is kept verbatim,
            // so the helper can only ever remove the one flag it names.
            _ = kept.Append(flags, index, flags.Length - index);

            break;

        }

        return DaclMarker + kept + (aclStart < 0 ? string.Empty : sddl[aclStart..]);

    }

}
