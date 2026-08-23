using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace RetroDownfall.Arcanum.Secrets.Security;

/// <summary>
/// The fixed host-tools marker slot on macOS, held as a retained <c>SecKeychainItemRef</c>.
/// </summary>
/// <remarks>
/// Security.framework hands back a retained item reference from every generic-password lookup, and
/// the ordinary credential store releases it immediately because it only wants the bytes. This arm
/// keeps it, and that is the entire difference: a delete that found its target again by service and
/// account would delete whatever now answers to that name, which after a byte-identical live
/// replacement is a different keychain item that was never compared.
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed partial class MacOsHostProcessToolsMarkerSlot
    : IHostProcessToolsMarkerCredentialCapabilitySource
{

    private const int ErrSecSuccess = 0;

    private const int ErrSecItemNotFound = -25300;

    public HostProcessToolsMarkerCredentialOpenResult OpenFixedSlot()
    {

        byte[] serviceBytes = Encoding.UTF8.GetBytes(HostProcessToolsMarkerSlotIdentity.Service);

        byte[] accountBytes = Encoding.UTF8.GetBytes(HostProcessToolsMarkerSlotIdentity.Account);

        int status = SecKeychainFindGenericPassword(
            nint.Zero,
            (uint)serviceBytes.Length,
            serviceBytes,
            (uint)accountBytes.Length,
            accountBytes,
            out uint passwordLength,
            out nint passwordData,
            out nint itemRef);

        if (status == ErrSecItemNotFound)
        {

            Release(itemRef, passwordData);

            return HostProcessToolsMarkerCredentialOpenResult.Absent();

        }

        if (status != ErrSecSuccess)
        {

            Release(itemRef, passwordData);

            return HostProcessToolsMarkerCredentialOpenResult.Unavailable();

        }

        // A found item with no data is a slot somebody created without a payload: definitely there,
        // definitely unusable, and never to be reported as an absent marker.
        if (itemRef == nint.Zero
            || !HostProcessToolsMarkerSlotIdentity.TryCopyNative(
                passwordData,
                checked((int)passwordLength),
                out byte[] opened))
        {

            Release(itemRef, passwordData);

            return HostProcessToolsMarkerCredentialOpenResult.PresentInvalid();

        }

        if (passwordData != nint.Zero)
        {

            _ = SecKeychainItemFreeContent(nint.Zero, passwordData);

        }

        try
        {

            return HostProcessToolsMarkerCredentialOpenResult.Opened(
                HostProcessToolsMarkerCredentialCapability.CreateOwned(
                    opened,
                    new MacOsRetainedItem(itemRef)));

        }
        catch (ArgumentOutOfRangeException)
        {

            CFRelease(itemRef);

            return HostProcessToolsMarkerCredentialOpenResult.PresentInvalid();

        }
        finally
        {

            CryptographicOperations.ZeroMemory(opened);

        }

    }

    public HostProcessToolsMarkerCredentialAbsenceResult ProveFixedSlotDurablyAbsent()
    {

        SlotObservation first = Observe();

        if (first is SlotObservation.Unavailable)
        {

            return HostProcessToolsMarkerCredentialAbsenceResult.Unavailable();

        }

        if (first is SlotObservation.Present)
        {

            return HostProcessToolsMarkerCredentialAbsenceResult.Present();

        }

        Barrier();

        SlotObservation second = Observe();

        return second switch
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
    /// Keychain Services exposes no public flush or synchronize call: <c>SecKeychainItemDelete</c>
    /// completes against the keychain database before it returns, and there is no separate barrier
    /// to invoke. What the second observation adds is that it is a *fresh* lookup rather than a
    /// re-read of anything retained, so it cannot answer from state the first one produced. The full
    /// fence keeps the two reads from being reordered around each other by the runtime.
    /// </remarks>
    private static void Barrier() => Thread.MemoryBarrier();

    private static SlotObservation Observe()
    {

        byte[] serviceBytes = Encoding.UTF8.GetBytes(HostProcessToolsMarkerSlotIdentity.Service);

        byte[] accountBytes = Encoding.UTF8.GetBytes(HostProcessToolsMarkerSlotIdentity.Account);

        int status = SecKeychainFindGenericPassword(
            nint.Zero,
            (uint)serviceBytes.Length,
            serviceBytes,
            (uint)accountBytes.Length,
            accountBytes,
            out _,
            out nint passwordData,
            out nint itemRef);

        Release(itemRef, passwordData);

        return status switch
        {

            ErrSecItemNotFound => SlotObservation.NotFound,

            // An item that answers at all is present, whatever its data turned out to be.
            ErrSecSuccess => SlotObservation.Present,

            _ => SlotObservation.Unavailable,

        };

    }

    private static void Release(nint itemRef, nint passwordData)
    {

        if (passwordData != nint.Zero)
        {

            _ = SecKeychainItemFreeContent(nint.Zero, passwordData);

        }

        if (itemRef != nint.Zero)
        {

            CFRelease(itemRef);

        }

    }

    private enum SlotObservation : byte
    {

        NotFound = 1,

        Present = 2,

        Unavailable = 3,

    }

    /// <summary>
    /// One retained keychain item, rereadable and deletable without ever naming the slot again.
    /// </summary>
    /// <remarks>
    /// The reference is released exactly once, on the first disposal, and a second disposal is a
    /// no-op — over-releasing a CFType is a process-wide corruption, not a leak.
    /// </remarks>
    private sealed class MacOsRetainedItem(nint itemRef) : IHostProcessToolsMarkerNativeRecordCapability
    {

        private nint _itemRef = itemRef;

        public HostProcessToolsMarkerCredentialDeleteStatus CompareDeleteExact(
            ReadOnlySpan<byte> expectedEncodedSecretUtf8)
        {

            if (_itemRef == nint.Zero)
            {

                return HostProcessToolsMarkerCredentialDeleteStatus.Unavailable;

            }

            // The reread goes through the retained reference, not the slot name. An item replaced
            // since the open is a different reference, and this one either still holds the compared
            // bytes or has been deleted underneath us — never somebody else's replacement.
            int status = SecKeychainItemCopyAttributesAndData(
                _itemRef,
                nint.Zero,
                nint.Zero,
                nint.Zero,
                out uint length,
                out nint data);

            if (status == ErrSecItemNotFound)
            {

                return HostProcessToolsMarkerCredentialDeleteStatus.Mismatch;

            }

            if (status != ErrSecSuccess)
            {

                return HostProcessToolsMarkerCredentialDeleteStatus.Unavailable;

            }

            byte[] current = [];

            bool equal;

            try
            {

                equal = HostProcessToolsMarkerSlotIdentity.TryCopyNative(
                        data,
                        checked((int)length),
                        out current)
                    && CryptographicOperations.FixedTimeEquals(expectedEncodedSecretUtf8, current);

            }
            finally
            {

                if (data != nint.Zero)
                {

                    _ = SecKeychainItemFreeAttributesAndData(nint.Zero, data);

                }

                CryptographicOperations.ZeroMemory(current);

            }

            if (!equal)
            {

                return HostProcessToolsMarkerCredentialDeleteStatus.Mismatch;

            }

            int deleted = SecKeychainItemDelete(_itemRef);

            if (deleted != ErrSecSuccess && deleted != ErrSecItemNotFound)
            {

                return HostProcessToolsMarkerCredentialDeleteStatus.Unavailable;

            }

            Barrier();

            return Observe() switch
            {

                SlotObservation.NotFound => HostProcessToolsMarkerCredentialDeleteStatus.Deleted,

                // Something answers the slot again already. The delete may well have succeeded, but
                // what is there now is not this operation's to reason about.
                SlotObservation.Present => HostProcessToolsMarkerCredentialDeleteStatus.Mismatch,

                _ => HostProcessToolsMarkerCredentialDeleteStatus.Unavailable,

            };

        }

        public void Dispose()
        {

            nint held = _itemRef;

            _itemRef = nint.Zero;

            if (held != nint.Zero)
            {

                CFRelease(held);

            }

        }

    }

    [LibraryImport("/System/Library/Frameworks/Security.framework/Security")]
    private static partial int SecKeychainFindGenericPassword(
        nint keychain,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        out uint passwordLength,
        out nint passwordData,
        out nint itemRef);

    [LibraryImport("/System/Library/Frameworks/Security.framework/Security")]
    private static partial int SecKeychainItemCopyAttributesAndData(
        nint itemRef,
        nint info,
        nint itemClass,
        nint attrList,
        out uint length,
        out nint outData);

    [LibraryImport("/System/Library/Frameworks/Security.framework/Security")]
    private static partial int SecKeychainItemFreeAttributesAndData(nint attrList, nint data);

    [LibraryImport("/System/Library/Frameworks/Security.framework/Security")]
    private static partial int SecKeychainItemDelete(nint itemRef);

    [LibraryImport("/System/Library/Frameworks/Security.framework/Security")]
    private static partial int SecKeychainItemFreeContent(nint attrList, nint data);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial void CFRelease(nint cf);

}
