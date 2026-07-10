using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace RetroDownfall.Arcanum.Secrets.Security;

/// <summary>Windows Credential Manager (Generic Credential) via advapi32.</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsOsCredentialStore
{

    private const int CredTypeGeneric = 1;

    private const int CredPersistLocalMachine = 2;

    private const int ErrorNotFound = 1168;

    internal static OsCredentialStoreResult TryGet(string service, string account)
    {

        string target = TargetName(service, account);

        if (!CredReadW(target, CredTypeGeneric, 0, out nint credentialPtr))
        {

            int error = Marshal.GetLastPInvokeError();

            return error == ErrorNotFound
                ? OsCredentialStoreResult.NotFound()
                : OsCredentialStoreResult.Failed($"CredReadW failed with error {error}.");

        }

        try
        {

            CREDENTIAL credential = Marshal.PtrToStructure<CREDENTIAL>(credentialPtr);

            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == nint.Zero)
            {

                return OsCredentialStoreResult.NotFound();

            }

            byte[] bytes = new byte[credential.CredentialBlobSize];

            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);

            string secret = Encoding.Unicode.GetString(bytes).TrimEnd('\0');

            return string.IsNullOrEmpty(secret)
                ? OsCredentialStoreResult.NotFound()
                : OsCredentialStoreResult.Ok(secret);

        }
        finally
        {

            CredFree(credentialPtr);

        }

    }

    internal static OsCredentialStoreResult Set(string service, string account, string secret)
    {

        string target = TargetName(service, account);

        byte[] blob = Encoding.Unicode.GetBytes(secret);

        nint blobPtr = Marshal.AllocHGlobal(blob.Length);

        nint targetPtr = Marshal.StringToCoTaskMemUni(target);

        nint userPtr = Marshal.StringToCoTaskMemUni(account);

        try
        {

            CREDENTIAL credential = new()
            {
                Type = CredTypeGeneric,
                TargetName = targetPtr,
                UserName = userPtr,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = CredPersistLocalMachine,
            };

            if (!CredWriteW(ref credential, 0))
            {

                int error = Marshal.GetLastPInvokeError();

                return OsCredentialStoreResult.Failed($"CredWriteW failed with error {error}.");

            }

            return OsCredentialStoreResult.Ok(secret);

        }
        finally
        {

            Marshal.FreeHGlobal(blobPtr);

            Marshal.FreeCoTaskMem(targetPtr);

            Marshal.FreeCoTaskMem(userPtr);

        }

    }

    internal static OsCredentialStoreResult Delete(string service, string account)
    {

        string target = TargetName(service, account);

        if (!CredDeleteW(target, CredTypeGeneric, 0))
        {

            int error = Marshal.GetLastPInvokeError();

            if (error == ErrorNotFound)
            {

                return OsCredentialStoreResult.Ok(string.Empty);

            }

            return OsCredentialStoreResult.Failed($"CredDeleteW failed with error {error}.");

        }

        return OsCredentialStoreResult.Ok(string.Empty);

    }

    private static string TargetName(string service, string account) => service + "/" + account;

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredReadW(string targetName, int type, int flags, out nint credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWriteW(ref CREDENTIAL credential, int flags);

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
