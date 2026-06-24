using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace RetroDownfall.Arcanum.Infrastructure.Theme;

/// <summary>
/// macOS-only CoreFoundation access for <c>AppleInterfaceStyle</c>. Returns whether the system UI is set to Dark.
/// </summary>
[ExcludeFromCodeCoverage] // Reason: macOS CoreFoundation interop, no portable test surface
internal static partial class MacOsThemeInterop
{

    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private static readonly IntPtr RtdlDefault = new(-2);

    private static IntPtr? _cachedAnyApplication;

    /// <summary>
    /// <c>true</c> when macOS reports Dark appearance; <c>false</c> for light or undecided (preference absent).
    /// Returns <c>true</c> when native calls fail (fail-safe dark).
    /// </summary>
    internal static bool TryGetSystemPrefersDark()
    {
        IntPtr keyCf = IntPtr.Zero;

        IntPtr valueCf = IntPtr.Zero;

        try
        {
            keyCf = CreateCfString("AppleInterfaceStyle");

            if (keyCf == IntPtr.Zero)
            {
                return true;
            }

            IntPtr anyApplication = GetCfPreferencesAnyApplication();

            if (anyApplication == IntPtr.Zero)
            {
                return true;
            }

            valueCf = CFPreferencesCopyAppValue(keyCf, anyApplication);

            if (valueCf == IntPtr.Zero)
            {
                return false;
            }

            if (CFGetTypeID(valueCf) != CFStringGetTypeID())
            {
                return false;
            }

            string? s = CfStringToManaged(valueCf);

            return s is not null && s.Equals("Dark", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
        finally
        {
            if (keyCf != IntPtr.Zero)
            {
                CFRelease(keyCf);
            }

            if (valueCf != IntPtr.Zero)
            {
                CFRelease(valueCf);
            }
        }
    }

    private static IntPtr GetCfPreferencesAnyApplication()
    {
        if (_cachedAnyApplication.HasValue)
        {
            return _cachedAnyApplication.Value;
        }

        IntPtr symbolAddress = Dlsym(RtdlDefault, "kCFPreferencesAnyApplication");

        if (symbolAddress == IntPtr.Zero)
        {
            _cachedAnyApplication = IntPtr.Zero;

            return IntPtr.Zero;
        }

        IntPtr cf = Marshal.ReadIntPtr(symbolAddress);

        _cachedAnyApplication = cf;

        return cf;
    }

    private static IntPtr CreateCfString(string value)
    {
        char[] chars = value.ToCharArray();

        GCHandle handle = GCHandle.Alloc(chars, GCHandleType.Pinned);

        try
        {
            return CFStringCreateWithCharacters(IntPtr.Zero, handle.AddrOfPinnedObject(), chars.Length);
        }
        finally
        {
            handle.Free();
        }
    }

    private static string? CfStringToManaged(IntPtr cfString)
    {
        nint length = CFStringGetLength(cfString);

        if (length <= 0)
        {
            return string.Empty;
        }

        if (length > 4096)
        {
            return null;
        }

        int charCount = (int)length;

        int byteLen = checked(charCount * sizeof(char));

        IntPtr unmanaged = Marshal.AllocHGlobal(byteLen);

        try
        {
            CFRange range = new() { Location = 0, Length = length };

            CFStringGetCharacters(cfString, range, unmanaged);

            return Marshal.PtrToStringUni(unmanaged, charCount);
        }
        finally
        {
            Marshal.FreeHGlobal(unmanaged);
        }
    }

    [LibraryImport("libSystem.B.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr dlsym(IntPtr handle, string symbol);

    private static IntPtr Dlsym(IntPtr handle, string symbol) => dlsym(handle, symbol);

    [LibraryImport(CoreFoundation)]
    private static partial IntPtr CFPreferencesCopyAppValue(IntPtr key, IntPtr applicationId);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFGetTypeID(IntPtr cf);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFStringGetTypeID();

    [LibraryImport(CoreFoundation)]
    private static partial IntPtr CFStringCreateWithCharacters(IntPtr alloc, IntPtr characters, nint numChars);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFStringGetLength(IntPtr theString);

    [LibraryImport(CoreFoundation)]
    private static partial void CFStringGetCharacters(IntPtr theString, CFRange range, IntPtr buffer);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(IntPtr cf);

    [StructLayout(LayoutKind.Sequential)]
    private struct CFRange
    {

        public nint Location;

        public nint Length;

    }

}

