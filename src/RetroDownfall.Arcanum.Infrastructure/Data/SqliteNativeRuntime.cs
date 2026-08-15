using System.Runtime.InteropServices;
using SQLitePCL;

namespace RetroDownfall.Arcanum.Infrastructure.Data;

/// <summary>
/// The process-wide SQLCipher provider.
/// </summary>
/// <remarks>
/// Replaces the former <c>Batteries_V2.Init()</c> calls. The bundle those came from installed a
/// provider over native libraries Arcanum neither built nor verified; this installs the single
/// hermetic library delivered for the running RID and then freezes the selection.
/// </remarks>
internal sealed class SqliteNativeRuntime : ISqliteNativeRuntime
{

    /// <summary>
    /// The one runtime for the process. Static entry points that cannot take a dependency — design
    /// time factories, compatibility facades — use this directly; composed components take
    /// <see cref="ISqliteNativeRuntime" />.
    /// </summary>
    public static SqliteNativeRuntime Instance { get; } = new();

    /// <summary>
    /// Execution-and-publication semantics: a second caller blocks until the first has finished
    /// installing, so no thread can observe a half-installed provider.
    /// </summary>
    private static readonly Lazy<bool> Initialized =
        new(InitializeCore, LazyThreadSafetyMode.ExecutionAndPublication);

    public void Initialize() => _ = Initialized.Value;

    private static bool InitializeCore()
    {

        try
        {

            raw.SetProvider(new SQLite3Provider_e_sqlcipher());

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

    /// <summary>
    /// The exact filename the delivery is expected to have placed next to the application. Used for
    /// diagnostics only; nothing here probes for it.
    /// </summary>
    private static string ExpectedAssetFileName()
    {

        if (OperatingSystem.IsWindows())
        {

            return "e_sqlcipher.dll";

        }

        if (OperatingSystem.IsMacOS())
        {

            return "libe_sqlcipher.dylib";

        }

        return "libe_sqlcipher.so";

    }

}
