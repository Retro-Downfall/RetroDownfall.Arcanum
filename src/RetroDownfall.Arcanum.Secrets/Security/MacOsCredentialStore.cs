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

        int status = SecKeychainAddGenericPassword(
            nint.Zero,
            (uint)serviceBytes.Length,
            serviceBytes,
            (uint)accountBytes.Length,
            accountBytes,
            (uint)secretBytes.Length,
            secretBytes,
            out _);

        if (status == ErrSecDuplicateItem)
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

                status = SecKeychainItemModifyAttributesAndData(
                    itemRef,
                    nint.Zero,
                    (uint)secretBytes.Length,
                    secretBytes);

            }
            finally
            {

                CFRelease(itemRef);

            }

        }

        if (status != ErrSecSuccess)
        {

            return OsCredentialStoreResult.Failed($"SecKeychain write failed with status {status}.");

        }

        return OsCredentialStoreResult.Ok(secret);

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

    [LibraryImport("/System/Library/Frameworks/Security.framework/Security")]
    private static partial int SecKeychainItemModifyAttributesAndData(
        nint itemRef,
        nint attrList,
        uint length,
        byte[] data);

    [LibraryImport("/System/Library/Frameworks/Security.framework/Security")]
    private static partial int SecKeychainItemDelete(nint itemRef);

    [LibraryImport("/System/Library/Frameworks/Security.framework/Security")]
    private static partial int SecKeychainItemFreeContent(nint attrList, nint data);

    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static partial void CFRelease(nint cf);

}
