using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace RetroDownfall.Arcanum.Secrets.Security;

/// <summary>macOS Keychain Services (generic password) via Security.framework.</summary>
[SupportedOSPlatform("macos")]
internal static partial class MacOsCredentialStore
{
    private const int ErrSecSuccess = 0;

    private const int ErrSecItemNotFound = -25300;

    private const int ErrSecDuplicateItem = -25299;

    private const uint LabelItemAttributeTag = 0x6C61626C;

    private const string MasterApiKeyDisplayLabel = "Arcanum — Server authentication";

    private const string FileEncryptionKeyDisplayLabel =
        "Arcanum — Attachment and file encryption";

    /// <summary>
    /// Looks up only the Keychain item reference. Null password output pointers are deliberate: the
    /// startup fast path needs existence metadata, never the protected password bytes that can
    /// trigger an authorization prompt.
    /// </summary>
    internal static OsCredentialStoreStatus ProbePresence(string service, string account)
    {
        byte[] serviceBytes = Encoding.UTF8.GetBytes(service);

        byte[] accountBytes = Encoding.UTF8.GetBytes(account);

        int status = SecKeychainFindGenericPasswordMetadata(
            nint.Zero,
            (uint)serviceBytes.Length,
            serviceBytes,
            (uint)accountBytes.Length,
            accountBytes,
            nint.Zero,
            nint.Zero,
            out nint itemRef);

        if (itemRef != nint.Zero)
        {
            CFRelease(itemRef);
        }

        return status switch
        {
            ErrSecSuccess => OsCredentialStoreStatus.Ok,
            ErrSecItemNotFound => OsCredentialStoreStatus.NotFound,
            _ => OsCredentialStoreStatus.Failed,
        };
    }

    internal static OsCredentialStoreResult TryGet(string service, string account)
    {
        byte[] serviceBytes = Encoding.UTF8.GetBytes(service);

        byte[] accountBytes = Encoding.UTF8.GetBytes(account);

        // Security.framework returns the item ref retained, so a lookup that discards it leaks a
        // CFType on every read. This path only needs the password bytes, so the ref is released
        // before anything else can return.
        int status = SecKeychainFindGenericPassword(
            nint.Zero,
            (uint)serviceBytes.Length,
            serviceBytes,
            (uint)accountBytes.Length,
            accountBytes,
            out uint passwordLength,
            out nint passwordData,
            out nint itemRef);

        if (itemRef != nint.Zero)
        {
            CFRelease(itemRef);
        }

        if (status == ErrSecItemNotFound)
        {
            return OsCredentialStoreResult.NotFound();
        }

        if (status != ErrSecSuccess)
        {
            return OsCredentialStoreResult.Failed($"SecKeychainFindGenericPassword failed with status {status}.");
        }

        try
        {
            if (passwordLength == 0 || passwordData == nint.Zero)
            {
                return OsCredentialStoreResult.NotFound();
            }

            byte[] bytes = new byte[passwordLength];

            Marshal.Copy(passwordData, bytes, 0, (int)passwordLength);

            return OsCredentialStoreResult.Ok(Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            if (passwordData != nint.Zero)
            {
                SecKeychainItemFreeContent(nint.Zero, passwordData);
            }
        }
    }

    internal static OsCredentialStoreResult Set(string service, string account, string secret)
    {
        byte[] serviceBytes = Encoding.UTF8.GetBytes(service);

        byte[] accountBytes = Encoding.UTF8.GetBytes(account);

        byte[] secretBytes = Encoding.UTF8.GetBytes(secret);

        // The item ref comes back retained here too, and an `out _` is a call-site discard only —
        // the generated stub still hands Security.framework the address of a real local, so the
        // reference is created either way. Released before anything else can return, exactly as the
        // lookup above does; a fresh account is written on every provider re-add, marker re-mint and
        // purged-then-restored credential, so a discarded one accumulates for the life of the host.
        int status = SecKeychainAddGenericPassword(
            nint.Zero,
            (uint)serviceBytes.Length,
            serviceBytes,
            (uint)accountBytes.Length,
            accountBytes,
            (uint)secretBytes.Length,
            secretBytes,
            out nint addedRef);

        if (status == ErrSecDuplicateItem)
        {
            if (addedRef != nint.Zero)
            {
                CFRelease(addedRef);
            }

            return UpdateExisting(
                service,
                account,
                secret,
                serviceBytes,
                accountBytes,
                secretBytes);
        }

        try
        {
            if (status != ErrSecSuccess)
            {
                return OsCredentialStoreResult.Failed($"SecKeychain write failed with status {status}.");
            }

            return CompleteSuccessfulWrite(
                service,
                account,
                secret,
                addedRef,
                TrySetDisplayLabel);
        }
        finally
        {
            if (addedRef != nint.Zero)
            {
                CFRelease(addedRef);
            }
        }
    }

    internal static string? DisplayLabelFor(string service, string account)
    {
        if (!string.Equals(service, ArcanumCredentialIdentity.Service, StringComparison.Ordinal))
        {
            return null;
        }

        return account switch
        {
            ArcanumCredentialIdentity.MasterApiKeyAccount => MasterApiKeyDisplayLabel,
            ArcanumCredentialIdentity.FileEncryptionKeyAccount => FileEncryptionKeyDisplayLabel,
            _ => null,
        };
    }

    /// <summary>
    /// Applies optional display metadata after the secret write has already succeeded. Keychain
    /// rejects metadata through an OSStatus return, and that cosmetic rejection must not turn the
    /// completed secret write into a reported failure.
    /// </summary>
    internal static OsCredentialStoreResult CompleteSuccessfulWrite(
        string service,
        string account,
        string secret,
        nint itemRef,
        Func<nint, string, int> trySetDisplayLabel)
    {
        string? displayLabel = DisplayLabelFor(service, account);

        if (itemRef != nint.Zero && displayLabel is not null)
        {
            try
            {
                _ = trySetDisplayLabel(itemRef, displayLabel);
            }
            catch (Exception exception) when (
                exception is DllNotFoundException
                    or EntryPointNotFoundException
                    or BadImageFormatException
                    or MarshalDirectiveException
                    or TypeLoadException)
            {
                // The secret is already durably written. The label is explanatory metadata, so a
                // missing or unusable interop surface cannot truthfully turn that write into a
                // failure and send callers down credential-replacement recovery paths.
            }
        }

        return OsCredentialStoreResult.Ok(secret);
    }

    private static OsCredentialStoreResult UpdateExisting(
        string service,
        string account,
        string secret,
        byte[] serviceBytes,
        byte[] accountBytes,
        byte[] secretBytes)
    {
        OsCredentialStoreResult existing = TryGetItemRef(serviceBytes, accountBytes, out nint itemRef);

        if (existing.Status != OsCredentialStoreStatus.Ok || itemRef == nint.Zero)
        {
            return existing.Status == OsCredentialStoreStatus.Ok
                ? OsCredentialStoreResult.Failed("Duplicate keychain item could not be updated.")
                : existing;
        }

        try
        {
            int status = SecKeychainItemModifyData(
                itemRef,
                nint.Zero,
                (uint)secretBytes.Length,
                secretBytes);

            if (status != ErrSecSuccess)
            {
                return OsCredentialStoreResult.Failed($"SecKeychain write failed with status {status}.");
            }

            return CompleteSuccessfulWrite(
                service,
                account,
                secret,
                itemRef,
                TrySetDisplayLabel);
        }
        finally
        {
            CFRelease(itemRef);
        }
    }

    private static unsafe int TrySetDisplayLabel(nint itemRef, string label)
    {
        byte[] labelBytes = Encoding.UTF8.GetBytes(label);

        fixed (byte* labelData = labelBytes)
        {
            SecKeychainAttribute attribute = new()
            {
                Tag = LabelItemAttributeTag,
                Length = (uint)labelBytes.Length,
                Data = (nint)labelData,
            };

            SecKeychainAttributeList attributes = new()
            {
                Count = 1,
                Attributes = (nint)(&attribute),
            };

            return SecKeychainItemModifyAttributes(
                itemRef,
                ref attributes,
                0,
                nint.Zero);
        }
    }

    internal static OsCredentialStoreResult Delete(string service, string account)
    {
        byte[] serviceBytes = Encoding.UTF8.GetBytes(service);

        byte[] accountBytes = Encoding.UTF8.GetBytes(account);

        OsCredentialStoreResult existing = TryGetItemRef(serviceBytes, accountBytes, out nint itemRef);

        if (existing.Status == OsCredentialStoreStatus.NotFound)
        {
            return OsCredentialStoreResult.Ok(string.Empty);
        }

        if (existing.Status != OsCredentialStoreStatus.Ok || itemRef == nint.Zero)
        {
            return existing;
        }

        try
        {
            int status = SecKeychainItemDelete(itemRef);

            if (status != ErrSecSuccess && status != ErrSecItemNotFound)
            {
                return OsCredentialStoreResult.Failed($"SecKeychainItemDelete failed with status {status}.");
            }

            return OsCredentialStoreResult.Ok(string.Empty);
        }
        finally
        {
            CFRelease(itemRef);
        }
    }

    private static OsCredentialStoreResult TryGetItemRef(byte[] serviceBytes, byte[] accountBytes, out nint itemRef)
    {
        int status = SecKeychainFindGenericPassword(
            nint.Zero,
            (uint)serviceBytes.Length,
            serviceBytes,
            (uint)accountBytes.Length,
            accountBytes,
            out _,
            out nint passwordData,
            out itemRef);

        if (passwordData != nint.Zero)
        {
            SecKeychainItemFreeContent(nint.Zero, passwordData);
        }

        if (status == ErrSecItemNotFound)
        {
            itemRef = nint.Zero;

            return OsCredentialStoreResult.NotFound();
        }

        if (status != ErrSecSuccess)
        {
            itemRef = nint.Zero;

            return OsCredentialStoreResult.Failed($"SecKeychainFindGenericPassword failed with status {status}.");
        }

        return OsCredentialStoreResult.Ok(string.Empty);
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

    [LibraryImport(
        "/System/Library/Frameworks/Security.framework/Security",
        EntryPoint = "SecKeychainFindGenericPassword")]
    private static partial int SecKeychainFindGenericPasswordMetadata(
        nint keychain,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        nint passwordLength,
        nint passwordData,
        out nint itemRef);

    [LibraryImport("/System/Library/Frameworks/Security.framework/Security")]
    private static partial int SecKeychainAddGenericPassword(
        nint keychain,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        uint passwordLength,
        byte[] passwordData,
        out nint itemRef);

    [LibraryImport(
        "/System/Library/Frameworks/Security.framework/Security",
        EntryPoint = "SecKeychainItemModifyAttributesAndData")]
    private static partial int SecKeychainItemModifyData(
        nint itemRef,
        nint attrList,
        uint length,
        byte[] data);

    [LibraryImport(
        "/System/Library/Frameworks/Security.framework/Security",
        EntryPoint = "SecKeychainItemModifyAttributesAndData")]
    private static partial int SecKeychainItemModifyAttributes(
        nint itemRef,
        ref SecKeychainAttributeList attrList,
        uint length,
        nint data);

    [LibraryImport("/System/Library/Frameworks/Security.framework/Security")]
    private static partial int SecKeychainItemDelete(nint itemRef);

    [LibraryImport("/System/Library/Frameworks/Security.framework/Security")]
    private static partial int SecKeychainItemFreeContent(nint attrList, nint data);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial void CFRelease(nint cf);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecKeychainAttribute
    {
        public uint Tag;

        public uint Length;

        public nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecKeychainAttributeList
    {
        public uint Count;

        public nint Attributes;
    }
}
