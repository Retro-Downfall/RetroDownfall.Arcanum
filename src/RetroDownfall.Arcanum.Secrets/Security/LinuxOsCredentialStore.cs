using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RetroDownfall.Arcanum.Secrets.Security;

/// <summary>Linux freedesktop Secret Service via libsecret-1.</summary>
[SupportedOSPlatform("linux")]
internal static partial class LinuxOsCredentialStore
{

    private static readonly object SchemaGate = new();

    private static nint _schema;

    private static bool _libMissing;

    internal static bool ProbeAvailable()
    {

        try
        {

            return EnsureSchema() != nint.Zero;

        }
        catch (DllNotFoundException)
        {

            _libMissing = true;

            return false;

        }
        catch (EntryPointNotFoundException)
        {

            _libMissing = true;

            return false;

        }

    }

    internal static OsCredentialStoreResult TryGet(string service, string account)
    {

        if (!TryEnsureSchema(out nint schema, out OsCredentialStoreResult unavailable))
        {

            return unavailable;

        }

        nint errorPtr = nint.Zero;

        nint passwordPtr = secret_password_lookup_sync(
            schema,
            nint.Zero,
            ref errorPtr,
            "service",
            service,
            "account",
            account,
            nint.Zero);

        if (errorPtr != nint.Zero)
        {

            string message = ReadGError(errorPtr);

            g_error_free(errorPtr);

            return OsCredentialStoreResult.Failed(message);

        }

        if (passwordPtr == nint.Zero)
        {

            return OsCredentialStoreResult.NotFound();

        }

        try
        {

            string? secret = Marshal.PtrToStringUTF8(passwordPtr);

            return string.IsNullOrEmpty(secret)
                ? OsCredentialStoreResult.NotFound()
                : OsCredentialStoreResult.Ok(secret);

        }
        finally
        {

            secret_password_free(passwordPtr);

        }

    }

    internal static OsCredentialStoreResult Set(string service, string account, string secret)
    {

        if (!TryEnsureSchema(out nint schema, out OsCredentialStoreResult unavailable))
        {

            return unavailable;

        }

        nint errorPtr = nint.Zero;

        int ok = secret_password_store_sync(
            schema,
            "default",
            $"{service}/{account}",
            secret,
            nint.Zero,
            ref errorPtr,
            "service",
            service,
            "account",
            account,
            nint.Zero);

        if (errorPtr != nint.Zero)
        {

            string message = ReadGError(errorPtr);

            g_error_free(errorPtr);

            return OsCredentialStoreResult.Failed(message);

        }

        return ok != 0
            ? OsCredentialStoreResult.Ok(secret)
            : OsCredentialStoreResult.Failed("secret_password_store_sync returned false.");

    }

    internal static OsCredentialStoreResult Delete(string service, string account)
    {

        if (!TryEnsureSchema(out nint schema, out OsCredentialStoreResult unavailable))
        {

            return unavailable;

        }

        nint errorPtr = nint.Zero;

        _ = secret_password_clear_sync(
            schema,
            nint.Zero,
            ref errorPtr,
            "service",
            service,
            "account",
            account,
            nint.Zero);

        if (errorPtr != nint.Zero)
        {

            string message = ReadGError(errorPtr);

            g_error_free(errorPtr);

            return OsCredentialStoreResult.Failed(message);

        }

        return OsCredentialStoreResult.Ok(string.Empty);

    }

    private static bool TryEnsureSchema(out nint schema, out OsCredentialStoreResult unavailable)
    {

        unavailable = default;

        try
        {

            schema = EnsureSchema();

        }
        catch (DllNotFoundException)
        {

            schema = nint.Zero;

            unavailable = OsCredentialStoreResult.Unavailable(
                "libsecret-1 is not installed. Install libsecret and ensure a Secret Service (e.g. gnome-keyring) is running.");

            return false;

        }
        catch (EntryPointNotFoundException ex)
        {

            schema = nint.Zero;

            unavailable = OsCredentialStoreResult.Unavailable($"libsecret entry point missing: {ex.Message}");

            return false;

        }

        if (schema == nint.Zero)
        {

            unavailable = OsCredentialStoreResult.Unavailable("libsecret schema could not be created.");

            return false;

        }

        return true;

    }

    private static nint EnsureSchema()
    {

        lock (SchemaGate)
        {

            if (_libMissing)
            {

                return nint.Zero;

            }

            if (_schema != nint.Zero)
            {

                return _schema;

            }

            // SECRET_SCHEMA_ATTRIBUTE_STRING = 0
            _schema = secret_schema_new(
                "org.retrodownfall.arcanum.MasterApiKey",
                0,
                "service",
                0,
                "account",
                0,
                nint.Zero);

            return _schema;

        }

    }

    private static string ReadGError(nint errorPtr)
    {

        // GError: domain (uint32), code (int32), message (char*) — message at offset 8 on LP64.
        nint messagePtr = Marshal.ReadIntPtr(errorPtr, 8);

        return Marshal.PtrToStringUTF8(messagePtr) ?? "libsecret error";

    }

    [LibraryImport("libsecret-1.so.0", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint secret_schema_new(
        string name,
        int flags,
        string attribute1Name,
        int attribute1Type,
        string attribute2Name,
        int attribute2Type,
        nint end);

    [LibraryImport("libsecret-1.so.0", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint secret_password_lookup_sync(
        nint schema,
        nint cancellable,
        ref nint error,
        string attribute1Name,
        string attribute1Value,
        string attribute2Name,
        string attribute2Value,
        nint end);

    [LibraryImport("libsecret-1.so.0", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int secret_password_store_sync(
        nint schema,
        string collection,
        string label,
        string password,
        nint cancellable,
        ref nint error,
        string attribute1Name,
        string attribute1Value,
        string attribute2Name,
        string attribute2Value,
        nint end);

    [LibraryImport("libsecret-1.so.0", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int secret_password_clear_sync(
        nint schema,
        nint cancellable,
        ref nint error,
        string attribute1Name,
        string attribute1Value,
        string attribute2Name,
        string attribute2Value,
        nint end);

    [LibraryImport("libsecret-1.so.0")]
    private static partial void secret_password_free(nint password);

    [LibraryImport("libglib-2.0.so.0")]
    private static partial void g_error_free(nint error);

}
