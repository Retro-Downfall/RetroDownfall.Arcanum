using System.Reflection;
using System.Runtime.InteropServices;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// The process-wide SQLCipher provider, bound exclusively to Arcanum's delivered native asset.
/// </summary>
internal sealed partial class SqliteNativeRuntime : ISqliteNativeRuntime
{
    public static SqliteNativeRuntime Instance { get; } = new();

    private static readonly object InitializationGate = new();

    private static readonly Lazy<bool> Initialized =
        new(InitializeCore, LazyThreadSafetyMode.ExecutionAndPublication);

    private static string _assetDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));

    // The resolver and native callbacks remain valid for the whole process lifetime.
    private static IntPtr _nativeHandle;

    public void Initialize()
    {
        lock (InitializationGate)
        {
            _ = Initialized.Value;
        }
    }

    /// <summary>
    /// EF tooling's application directory is the tool directory. Select the target assembly's
    /// delivered asset before the same one-time initialization; no search path is introduced.
    /// </summary>
    internal void InitializeForDesignTime(string assemblyDirectory)
    {
        lock (InitializationGate)
        {
            string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(assemblyDirectory));

            if (Initialized.IsValueCreated && !string.Equals(_assetDirectory, directory, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The SQLCipher runtime has already selected its native asset.");
            }

            _assetDirectory = directory;

            _ = Initialized.Value;
        }
    }

    private static bool InitializeCore()
    {
        try
        {
            if (!(OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                && !(OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64))
            {
                throw new DllNotFoundException("No SQLCipher asset is delivered for this platform.");
            }

            string assetPath = Path.GetFullPath(Path.Combine(_assetDirectory, ExpectedAssetFileName()));

            if (!File.Exists(assetPath))
            {
                throw new DllNotFoundException("The delivered SQLCipher asset is absent.");
            }

            // dyld can redirect even an absolute dlopen path. Refuse active overrides before load.
            if (OperatingSystem.IsMacOS() && new[]
                {
                    "DYLD_LIBRARY_PATH", "DYLD_FALLBACK_LIBRARY_PATH", "DYLD_VERSIONED_LIBRARY_PATH",
                    "DYLD_IMAGE_SUFFIX", "DYLD_ROOT_PATH",
                }.Any(static name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))))
            {
                throw new DllNotFoundException("Native loader overrides cannot select the SQLCipher asset.");
            }

            IntPtr handle = NativeLibrary.Load(assetPath);

            try
            {
                ValidateNativeImage(handle, assetPath);
            }
            catch
            {
                NativeLibrary.Free(handle);

                throw;
            }

            _nativeHandle = handle;

            NativeLibrary.SetDllImportResolver(typeof(SQLite3Provider_sqlcipher).Assembly, ResolveImport);

            // SetProvider calls sqlite3_libversion_number immediately, so the resolver must exist.
            raw.SetProvider(new SQLite3Provider_sqlcipher());

            raw.FreezeProvider(true);

            return true;
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or TypeInitializationException)
        {
            throw new SqliteNativeRuntimeUnavailableException(
                RuntimeInformation.RuntimeIdentifier,
                ExpectedAssetFileName(),
                exception);
        }
    }

    private static unsafe void ValidateNativeImage(IntPtr handle, string expectedPath)
    {
        string? loadedPath;

        if (OperatingSystem.IsMacOS())
        {
            IntPtr symbol = NativeLibrary.GetExport(handle, "sqlite3_libversion_number");

            if (GetNativeImageInfo(symbol, out NativeImageInfo image) == 0)
            {
                throw new DllNotFoundException("The SQLCipher native image could not be identified.");
            }

            loadedPath = Marshal.PtrToStringUTF8(image.FileName);
        }
        else
        {
            // Windows extended-length paths have a maximum of 32,767 UTF-16 characters.
            const int capacity = 32768;

            char* buffer = stackalloc char[capacity];

            uint length = GetModuleFileName(handle, buffer, capacity);

            if (length == 0 || length >= capacity)
            {
                throw new DllNotFoundException("The SQLCipher native image could not be identified.");
            }

            loadedPath = new string(buffer, 0, (int)length);
        }

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (loadedPath is null || !string.Equals(Path.GetFullPath(loadedPath), expectedPath, comparison))
        {
            throw new DllNotFoundException("The native loader substituted a different SQLCipher asset.");
        }
    }

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "dladdr")]
    private static partial int GetNativeImageInfo(IntPtr address, out NativeImageInfo image);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleFileNameW", SetLastError = true)]
    private static unsafe partial uint GetModuleFileName(IntPtr module, char* path, uint capacity);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeImageInfo
    {
        public IntPtr FileName;

        public IntPtr BaseAddress;

        public IntPtr SymbolName;

        public IntPtr SymbolAddress;
    }

    private static IntPtr ResolveImport(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != "sqlcipher" || _nativeHandle == IntPtr.Zero)
        {
            // Returning zero would authorize platform probing. This provider has one import only.
            throw new DllNotFoundException("The SQLCipher provider requested an unavailable native import.");
        }

        return _nativeHandle;
    }

    private static string ExpectedAssetFileName() =>
        OperatingSystem.IsWindows() ? "e_sqlcipher.dll" : "libe_sqlcipher.dylib";
}
