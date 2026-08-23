using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace RetroDownfall.Arcanum.Secrets.Security;

/// <summary>
/// The fixed host-tools marker slot on Windows, held as a snapshot of the complete credential.
/// </summary>
/// <remarks>
/// Credential Manager has no handle to retain — <c>CredReadW</c> hands back a buffer that
/// <c>CredFree</c> then releases, and <c>CredDeleteW</c> takes a target name rather than a
/// reference. The identity has to be reconstructed instead, and a blob comparison alone is not
/// enough: a replacement writing identical bytes would compare equal. The snapshot therefore also
/// carries <c>LastWritten</c>, which the credential service stamps on every write, so a rewrite
/// between the open and the delete is visible even when the value did not change.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsHostProcessToolsMarkerSlot
    : IHostProcessToolsMarkerCredentialCapabilitySource
{

    private const int CredTypeGeneric = 1;

    private const int ErrorNotFound = 1168;

    public HostProcessToolsMarkerCredentialOpenResult OpenFixedSlot()
    {

        CredentialSnapshot? snapshot = Read(out SlotObservation observation);

        if (observation is SlotObservation.NotFound)
        {

            return HostProcessToolsMarkerCredentialOpenResult.Absent();

        }

        if (observation is SlotObservation.Unavailable)
        {

            return HostProcessToolsMarkerCredentialOpenResult.Unavailable();

        }

        if (snapshot is not { } record
            || !HostProcessToolsMarkerSlotIdentity.TryEncode(record.Value, out byte[] encoded))
        {

            snapshot?.Clear();

            return HostProcessToolsMarkerCredentialOpenResult.PresentInvalid();

        }

        try
        {

            return HostProcessToolsMarkerCredentialOpenResult.Opened(
                HostProcessToolsMarkerCredentialCapability.CreateOwned(
                    encoded,
                    new WindowsRetainedRecord(record)));

        }
        catch (ArgumentOutOfRangeException)
        {

            record.Clear();

            return HostProcessToolsMarkerCredentialOpenResult.PresentInvalid();

        }
        finally
        {

            CryptographicOperations.ZeroMemory(encoded);

        }

    }

    public HostProcessToolsMarkerCredentialAbsenceResult ProveFixedSlotDurablyAbsent()
    {

        CredentialSnapshot? first = Read(out SlotObservation firstObservation);

        first?.Clear();

        if (firstObservation is SlotObservation.Unavailable)
        {

            return HostProcessToolsMarkerCredentialAbsenceResult.Unavailable();

        }

        if (firstObservation is SlotObservation.Present)
        {

            return HostProcessToolsMarkerCredentialAbsenceResult.Present();

        }

        Barrier();

        CredentialSnapshot? second = Read(out SlotObservation secondObservation);

        second?.Clear();

        return secondObservation switch
        {

            SlotObservation.NotFound => HostProcessToolsMarkerCredentialAbsenceResult.Absent(),

            SlotObservation.Present => HostProcessToolsMarkerCredentialAbsenceResult.Present(),

            _ => HostProcessToolsMarkerCredentialAbsenceResult.Unavailable(),

        };

    }

    /// <summary>
    /// The platform durability step this arm can honestly claim.
    /// </summary>
    /// <remarks>
    /// Credential Manager writes through on the call rather than behind a cache, and exposes no
    /// separate flush. What the second read adds is that it goes back to the credential service
    /// rather than to any buffer the first read produced — every one of them was already released.
    /// </remarks>
    private static void Barrier() => Thread.MemoryBarrier();

    private static CredentialSnapshot? Read(out SlotObservation observation)
    {

        string target = TargetName();

        if (!CredReadW(target, CredTypeGeneric, 0, out nint credentialPtr))
        {

            observation = Marshal.GetLastPInvokeError() == ErrorNotFound
                ? SlotObservation.NotFound
                : SlotObservation.Unavailable;

            return null;

        }

        try
        {

            CREDENTIAL credential = Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);

            observation = SlotObservation.Present;

            if (!HostProcessToolsMarkerSlotIdentity.TryCopyNative(
                    credential.CredentialBlob,
                    checked((int)credential.CredentialBlobSize),
                    out byte[] blob))
            {

                return null;

            }

            // The stored blob is UTF-16 with a trailing terminator, exactly as the ordinary store
            // writes it; the capability's own copy is the UTF-8 of that decoded value.
            string value = Encoding.Unicode.GetString(blob).TrimEnd('\0');

            return new CredentialSnapshot(blob, credential.LastWritten, value);

        }
        finally
        {

            CredFree(credentialPtr);

        }

    }

    private static string TargetName() =>
        HostProcessToolsMarkerSlotIdentity.Service + "/" + HostProcessToolsMarkerSlotIdentity.Account;

    private enum SlotObservation : byte
    {

        NotFound = 1,

        Present = 2,

        Unavailable = 3,

    }

    /// <summary>The complete credential record as it stood when the slot was opened.</summary>
    private sealed record CredentialSnapshot(byte[] Blob, long LastWritten, string Value)
    {

        internal void Clear() => CryptographicOperations.ZeroMemory(Blob);

    }

    private sealed class WindowsRetainedRecord(CredentialSnapshot opened)
        : IHostProcessToolsMarkerNativeRecordCapability
    {

        private CredentialSnapshot? _opened = opened;

        public HostProcessToolsMarkerCredentialDeleteStatus CompareDeleteExact(
            ReadOnlySpan<byte> expectedEncodedSecretUtf8)
        {

            if (_opened is not { } retained)
            {

                return HostProcessToolsMarkerCredentialDeleteStatus.Unavailable;

            }

            CredentialSnapshot? current = Read(out SlotObservation observation);

            try
            {

                if (observation is SlotObservation.Unavailable)
                {

                    return HostProcessToolsMarkerCredentialDeleteStatus.Unavailable;

                }

                // The complete record has to still be the one that was opened: same bytes, same
                // last-written stamp. Either alone would accept a rewrite.
                if (observation is SlotObservation.NotFound
                    || current is not { } live
                    || live.LastWritten != retained.LastWritten
                    || !CryptographicOperations.FixedTimeEquals(live.Blob, retained.Blob)
                    || !HostProcessToolsMarkerSlotIdentity.TryEncode(live.Value, out byte[] encoded))
                {

                    return HostProcessToolsMarkerCredentialDeleteStatus.Mismatch;

                }

                bool matches = CryptographicOperations.FixedTimeEquals(
                    expectedEncodedSecretUtf8,
                    encoded);

                CryptographicOperations.ZeroMemory(encoded);

                if (!matches)
                {

                    return HostProcessToolsMarkerCredentialDeleteStatus.Mismatch;

                }

            }
            finally
            {

                current?.Clear();

            }

            if (!CredDeleteW(TargetName(), CredTypeGeneric, 0)
                && Marshal.GetLastPInvokeError() != ErrorNotFound)
            {

                return HostProcessToolsMarkerCredentialDeleteStatus.Unavailable;

            }

            Barrier();

            CredentialSnapshot? readback = Read(out SlotObservation after);

            readback?.Clear();

            return after switch
            {

                SlotObservation.NotFound => HostProcessToolsMarkerCredentialDeleteStatus.Deleted,

                SlotObservation.Present => HostProcessToolsMarkerCredentialDeleteStatus.Mismatch,

                _ => HostProcessToolsMarkerCredentialDeleteStatus.Unavailable,

            };

        }

        public void Dispose()
        {

            CredentialSnapshot? held = _opened;

            _opened = null;

            held?.Clear();

        }

    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredReadW(string targetName, int type, int flags, out nint credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDeleteW(string targetName, int type, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(nint buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct CREDENTIAL
    {

        public uint Flags;

        public int Type;

        public nint TargetName;

        public nint Comment;

        public long LastWritten;

        public uint CredentialBlobSize;

        public nint CredentialBlob;

        public int Persist;

        public uint AttributeCount;

        public nint Attributes;

        public nint TargetAlias;

        public nint UserName;

    }

}
