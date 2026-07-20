using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Terminal.Gui.App;
using Terminal.Gui.Views;

namespace RetroDownfall.Arcanum.Cli.CommandCenter;

/// <summary>
/// Thin Terminal.Gui lifecycle wrapper. Sole allowed site for any AOT suppressions
/// related to Terminal.Gui bootstrap.
/// </summary>
internal sealed class CommandCenterApp(ILogger<CommandCenterApp> logger)
{
    /// <summary>
    /// Floor tuned for Warp's startup race (PTY often begins at command-block height,
    /// then SIGWINCHes to the full viewport). Absolute layout expands on ScreenChanged.
    /// </summary>
    public const int MinCols = 80;

    public const int MinRows = 12;

    /// <summary>
    /// Escape sequences to leave alt-screen / mouse tracking / hide-cursor modes.
    /// Required when Terminal.Gui crashes or Dispose fails — otherwise the shell keeps
    /// receiving SGR mouse reports (looks like red <c>35;16;1M</c> spam + beeps).
    /// </summary>
    private const string TerminalRestoreSequence =
        "\u001b[?1003l\u001b[?1006l\u001b[?1015l\u001b[?1000l\u001b[?1002l"
        + "\u001b[?2004l\u001b[?1049l\u001b[?25h\u001b[0m\u001b[?7h";

    /// <summary>Last size observed by <see cref="Run"/> (for host error messages).</summary>
    public int LastDetectedCols { get; private set; }

    /// <summary>Last size observed by <see cref="Run"/> (for host error messages).</summary>
    public int LastDetectedRows { get; private set; }

    /// <summary>Size floor checked after Terminal.Gui can report dimensions.</summary>
    public static bool IsViewportLargeEnough(int cols, int rows) =>
        cols >= MinCols && rows >= MinRows;

    /// <summary>
    /// Resolve real PTY size. Terminal.Gui 2.4.17 often reports 0×0 after Init on macOS
    /// (CSI 18t unanswered). Prefer driver/screen, then <c>stty size</c>, then Console/env.
    /// Avoids unsafe libc ioctl P/Invokes that AV under Native AOT.
    /// </summary>
    public static (int Cols, int Rows) ResolveViewportSize(IApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        int cols = Positive(app.Driver?.Cols ?? 0);
        int rows = Positive(app.Driver?.Rows ?? 0);

        PreferLarger(ref cols, ref rows, app.Screen.Width, app.Screen.Height);

        if (cols > 0 && rows > 0)
        {
            return (cols, rows);
        }

        if (TryGetSttySize(out int sttyCols, out int sttyRows))
        {
            PreferLarger(ref cols, ref rows, sttyCols, sttyRows);
        }

        PreferLarger(ref cols, ref rows, Positive(Console.WindowWidth), Positive(Console.WindowHeight));

        if (cols <= 0 || rows <= 0)
        {
            if (TryParsePositiveEnv("COLUMNS", out int envCols))
            {
                cols = Math.Max(cols, envCols);
            }

            if (TryParsePositiveEnv("LINES", out int envRows))
            {
                rows = Math.Max(rows, envRows);
            }
        }

        return (cols, rows);
    }

    /// <summary>
    /// Runs <paramref name="buildAndWire"/> after Init. Returns process exit code.
    /// -2 = too small; -1 = init/run failure.
    /// </summary>
    public int Run(Func<IApplication, CommandCenterWindow, int> buildAndWire, bool monochromeTheme = false)
    {
        IApplication? app = null;
        CommandCenterWindow? window = null;
        LastDetectedCols = 0;
        LastDetectedRows = 0;
        bool terminalTouched = false;

        try
        {
            app = CreateAndInit();
            terminalTouched = true;

            CommandCenterTheme.Apply(monochromeTheme);

            // Warp often starts the PTY at command-block height, then SIGWINCHes to full
            // viewport after alt-screen. Poll briefly for a stable driver size.
            (int cols, int rows) = WaitForStableViewport(app);
            EnsureDriverMatches(app, cols, rows);

            // Re-read after sync — layout must use the driver buffer size.
            (cols, rows) = ResolveViewportSize(app);
            LastDetectedCols = cols;
            LastDetectedRows = rows;

            if (!IsViewportLargeEnough(cols, rows))
            {
                return -2;
            }

            window = new CommandCenterWindow();
            window.ApplyAbsoluteLayout(cols, rows);
            return buildAndWire(app, window);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Terminal.Gui Command Center failed to start.");
            return -1;
        }
        finally
        {
            try
            {
                window?.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Command Center window dispose failed.");
            }

            try
            {
                app?.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Terminal.Gui dispose failed.");
            }

            if (terminalTouched)
            {
                RestoreTerminalModes();
            }
        }
    }

    /// <summary>Best-effort restore when TG leaves mouse/alt-screen modes enabled.</summary>
    public static void RestoreTerminalModes()
    {
        try
        {
            Console.Out.Write(TerminalRestoreSequence);
            Console.Out.Flush();
        }
        catch
        {
            // ignore
        }

        try
        {
            Console.Error.Write(TerminalRestoreSequence);
            Console.Error.Flush();
        }
        catch
        {
            // ignore
        }
    }

    private static (int Cols, int Rows) WaitForStableViewport(IApplication app)
    {
        (int cols, int rows) = ResolveViewportSize(app);
        for (int i = 0; i < 10; i++)
        {
            if (IsViewportLargeEnough(cols, rows))
            {
                break;
            }

            Thread.Sleep(30);
            (cols, rows) = ResolveViewportSize(app);
        }

        return (cols, rows);
    }

    private static void EnsureDriverMatches(IApplication app, int cols, int rows)
    {
        if (app.Driver is null || cols <= 0 || rows <= 0)
        {
            return;
        }

        if (app.Driver.Cols == cols && app.Driver.Rows == rows)
        {
            return;
        }

        try
        {
            app.Driver.SetScreenSize(cols, rows);
        }
        catch
        {
            // Layout still proceeds with absolute frames from ResolveViewportSize.
        }
    }

    private static void PreferLarger(ref int cols, ref int rows, int nextCols, int nextRows)
    {
        if (nextCols > cols)
        {
            cols = nextCols;
        }

        if (nextRows > rows)
        {
            rows = nextRows;
        }
    }

    private static int Positive(int value) => value > 0 ? value : 0;

    private static bool TryParsePositiveEnv(string name, out int value)
    {
        value = 0;
        string? raw = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, out value)
            && value > 0;
    }

    /// <summary>
    /// <c>stty size</c> prints <c>rows cols</c> against the controlling tty — AOT-safe.
    /// </summary>
    private static bool TryGetSttySize(out int cols, out int rows)
    {
        cols = 0;
        rows = 0;

        try
        {
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "stty",
                    Arguments = "size",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            if (!process.Start())
            {
                return false;
            }

            string output = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(1000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignore
                }

                return false;
            }

            string[] parts = output.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                return false;
            }

            // stty size → "ROWS COLS"
            if (!int.TryParse(parts[0], out rows) || !int.TryParse(parts[1], out cols))
            {
                cols = 0;
                rows = 0;
                return false;
            }

            return cols > 0 && rows > 0;
        }
        catch
        {
            cols = 0;
            rows = 0;
            return false;
        }
    }

    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "Terminal.Gui v2.4.17 initialization is isolated here; Arcanum Native AOT publish verified by ./scripts/verify-aot-il-warnings.sh.")]
    [UnconditionalSuppressMessage(
        "TrimAnalysis",
        "IL2026",
        Justification = "Terminal.Gui v2.4.17 initialization is isolated here; Arcanum Native AOT publish verified by ./scripts/verify-aot-il-warnings.sh.")]
    private static IApplication CreateAndInit()
    {
        // Keep mouse off — session picking is ↑/↓ + Enter in the Session log.
        // Avoid leaving ?1003h/?1006h tracking in the shell if TG crashes.
#pragma warning disable CS0618 // Legacy static still consulted during Init on 2.4.17.
        Application.IsMouseDisabled = true;
#pragma warning restore CS0618

        IApplication app = Application.Create();
        app.AppModel = AppModel.FullScreen;
        if (app.Mouse is not null)
        {
            app.Mouse.IsMouseDisabled = true;
        }

        _ = app.Init();

        if (app.Mouse is not null)
        {
            app.Mouse.IsMouseDisabled = true;
        }

        return app;
    }
}
