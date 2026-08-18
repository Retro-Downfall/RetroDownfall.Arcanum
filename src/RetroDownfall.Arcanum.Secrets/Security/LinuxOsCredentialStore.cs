using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RetroDownfall.Arcanum.Secrets.Security;

/// <summary>Linux freedesktop Secret Service via libsecret-1.</summary>
[SupportedOSPlatform("linux")]
internal static partial class LinuxOsCredentialStore
{

    private const int SecretServiceNone = 0;

    private static readonly object SchemaGate = new();

    private static nint _schema;

    private static bool _libMissing;

    /// <summary>
    /// Whether a Secret Service actually answers on this session's bus.
    /// </summary>
    /// <remarks>
    /// <see cref="ProbeAvailable"/> deliberately answers a narrower question: <c>secret_schema_new</c>
    /// is a client-side allocation that never touches D-Bus, so it succeeds on a headless host with
    /// no keyring daemon and no session bus at all. Obtaining the service is the first call that has
    /// to reach the bus, and it is the question every caller of <c>IsAvailable</c> is really asking —
    /// they use the answer to choose between failing a save closed and degrading it to the encrypted
    /// mirror. libsecret caches the service it hands back, so a healthy host pays for the connection
    /// once and every later probe is a reference count.
    /// <para>
    /// Only reachability is reported here. Read and write statuses are left exactly as libsecret
    /// described them, because <c>ArcanumMasterKeyBootstrapper</c> treats <c>Unavailable</c> as proof
    /// that no credential of ours is living in the backend and mints over it — a conclusion a
    /// transport error is not entitled to draw.
    /// </para>
    /// </remarks>
    internal static bool ProbeReachable()
    {

        if (!ProbeAvailable())
        {

            return false;

        }

        nint errorPtr = nint.Zero;

        nint service;

        try
        {

            service = secret_service_get_sync(SecretServiceNone, nint.Zero, ref errorPtr);

        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {

            return false;

        }

        bool reachable = errorPtr == nint.Zero && service != nint.Zero;

        if (errorPtr != nint.Zero)
        {

            g_error_free(errorPtr);

        }

        if (service != nint.Zero)
        {

            Unref(service);

        }

        return reachable;

    }

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

    /// <summary>
    /// Drops the probe's reference to the service without letting a missing GObject runtime turn a
    /// reachability question into a thrown exception.
    /// </summary>
    /// <remarks>
    /// A host that answered the probe has libgobject loaded already — libsecret cannot have returned
    /// a GObject without it — so the swallowed case is unreachable in practice. Leaking one cached
    /// service reference is still the better outcome than a probe that throws.
    /// </remarks>
    private static void Unref(nint instance)
    {

        try
        {

            g_object_unref(instance);

        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {

            // Deliberately ignored; see the remarks above.

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
    private static partial nint secret_service_get_sync(int flags, nint cancellable, ref nint error);

    [LibraryImport("libsecret-1.so.0")]
    private static partial void secret_password_free(nint password);

    [LibraryImport("libglib-2.0.so.0")]
    private static partial void g_error_free(nint error);

    [LibraryImport("libgobject-2.0.so.0")]
    private static partial void g_object_unref(nint instance);

}
