using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

namespace RetroDownfall.Arcanum.Tests.Process;

/// <summary>
/// The Windows broker launches its target with a non-null <c>lpApplicationName</c>, which Win32 uses
/// as-is: no PATH search, no default <c>.exe</c>. A bare <c>git</c> therefore has to become an
/// absolute path on the host, against the child's scrubbed PATH, before the payload is written. The
/// resolver is pure over PATH, PATHEXT and a file-exists probe so these cases run on every host.
/// </summary>
public sealed class WindowsBrokerTargetResolverTests
{
    private const string SearchPath =
        @"C:\Windows\system32;C:\Program Files\Git\cmd;C:\Users\dev\AppData\Roaming\npm;C:\Users\dev\.cargo\bin";

    private const string PathExt = ".COM;.EXE;.BAT;.CMD";

    private const string UserProfile = @"C:\Users\dev";

    [Fact]
    public void Bare_name_resolves_through_PATH_and_PATHEXT_to_an_absolute_path()
    {
        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Program Files\Git\cmd\git.exe",
            @"C:\Windows\system32\cmd.exe",
        };

        Result<WindowsBrokerTarget> git = WindowsBrokerTargetResolver.Resolve(
            "git",
            SearchPath,
            PathExt,
            files.Contains,
            userProfile: UserProfile);

        Result<WindowsBrokerTarget> cmd = WindowsBrokerTargetResolver.Resolve(
            "cmd",
            SearchPath,
            PathExt,
            files.Contains,
            userProfile: UserProfile);

        Assert.True(git.IsSuccess, git.IsFailure ? git.Error.Message : null);
        Assert.Equal(@"C:\Program Files\Git\cmd\git.exe", git.Value.Path, ignoreCase: true);

        // Program Files and the Windows directory are readable by every AppContainer already.
        Assert.Null(git.Value.ReadExecuteRoot);
        Assert.True(cmd.IsSuccess, cmd.IsFailure ? cmd.Error.Message : null);
        Assert.Equal(@"C:\Windows\system32\cmd.exe", cmd.Value.Path, ignoreCase: true);
    }

    [Fact]
    public void Name_with_an_executable_extension_is_searched_as_given()
    {
        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Program Files\Git\cmd\git.exe",
        };

        Result<WindowsBrokerTarget> result = WindowsBrokerTargetResolver.Resolve(
            "git.exe",
            SearchPath,
            PathExt,
            files.Contains);

        Assert.True(result.IsSuccess);
        Assert.Equal(@"C:\Program Files\Git\cmd\git.exe", result.Value.Path, ignoreCase: true);
    }

    [Fact]
    public void Missing_PATHEXT_falls_back_to_the_windows_default()
    {
        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Windows\system32\where.exe",
        };

        Result<WindowsBrokerTarget> result = WindowsBrokerTargetResolver.Resolve(
            "where",
            SearchPath,
            pathExt: null,
            files.Contains);

        Assert.True(result.IsSuccess);
        Assert.Equal(@"C:\Windows\system32\where.exe", result.Value.Path, ignoreCase: true);
    }

    [Fact]
    public void Unresolvable_name_returns_failure()
    {
        Result<WindowsBrokerTarget> result = WindowsBrokerTargetResolver.Resolve(
            "definitely-not-installed",
            SearchPath,
            PathExt,
            static _ => false);

        Assert.True(result.IsFailure);
        Assert.Equal(WindowsBrokerTargetResolver.CommandNotFoundCode, result.Error.Code);
        Assert.Contains("definitely-not-installed", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("not found", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Batch_shim_is_refused_rather_than_launched_through_cmd()
    {
        // npm installs `claude.cmd`; CreateProcessW cannot start a batch file as its application name, and
        // wrapping it in `cmd.exe /c` would re-parse model-supplied arguments through cmd's metacharacters.
        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Users\dev\AppData\Roaming\npm\claude.cmd",
        };

        Result<WindowsBrokerTarget> bare = WindowsBrokerTargetResolver.Resolve(
            "claude",
            SearchPath,
            PathExt,
            files.Contains);

        Result<WindowsBrokerTarget> explicitPath = WindowsBrokerTargetResolver.Resolve(
            @"C:\tools\build.bat",
            SearchPath,
            PathExt,
            static _ => true);

        Assert.True(bare.IsFailure);
        Assert.Equal(WindowsBrokerTargetResolver.BatchTargetRefusedCode, bare.Error.Code);
        Assert.Contains("cmd.exe /c", bare.Error.Message, StringComparison.Ordinal);
        Assert.True(explicitPath.IsFailure);
        Assert.Equal(WindowsBrokerTargetResolver.BatchTargetRefusedCode, explicitPath.Error.Code);
    }

    [Fact]
    public void Relative_PATH_entries_are_never_searched()
    {
        // `.` on PATH would resolve against the broker's or workspace's directory — a planted `git.exe`
        // in a cloned repository must not shadow the real one.
        Result<WindowsBrokerTarget> result = WindowsBrokerTargetResolver.Resolve(
            "git",
            @".;bin;C:\Program Files\Git\cmd",
            PathExt,
            static candidate => candidate.StartsWith(@".\", StringComparison.Ordinal)
                || candidate.StartsWith(@"bin\", StringComparison.Ordinal)
                || string.Equals(candidate, @"C:\Program Files\Git\cmd\git.exe", StringComparison.OrdinalIgnoreCase));

        Assert.True(result.IsSuccess);
        Assert.Equal(@"C:\Program Files\Git\cmd\git.exe", result.Value.Path, ignoreCase: true);
    }

    [Fact]
    public void Absolute_target_is_kept_and_relative_target_resolves_against_the_working_directory()
    {
        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase)
        {
            @"D:\tools\fmt.exe",
            @"C:\work\repo\bin\gen.exe",
        };

        Result<WindowsBrokerTarget> absolute = WindowsBrokerTargetResolver.Resolve(
            @"D:\tools\fmt",
            SearchPath,
            PathExt,
            files.Contains);

        Result<WindowsBrokerTarget> relative = WindowsBrokerTargetResolver.Resolve(
            @"bin\gen.exe",
            SearchPath,
            PathExt,
            files.Contains,
            workingDirectory: @"C:\work\repo");

        Result<WindowsBrokerTarget> relativeWithoutDirectory = WindowsBrokerTargetResolver.Resolve(
            @"bin\gen.exe",
            SearchPath,
            PathExt,
            files.Contains);

        Assert.True(absolute.IsSuccess);
        Assert.Equal(@"D:\tools\fmt.exe", absolute.Value.Path, ignoreCase: true);
        Assert.Null(absolute.Value.ReadExecuteRoot);
        Assert.True(relative.IsSuccess);
        Assert.Equal(@"C:\work\repo\bin\gen.exe", relative.Value.Path, ignoreCase: true);
        Assert.True(relativeWithoutDirectory.IsFailure);
    }

    [Fact]
    public void Executable_found_on_PATH_under_the_user_profile_names_its_directory_for_read_execute()
    {
        // An AppContainer cannot read %USERPROFILE%, so cargo/scoop/pyenv binaries need their directory
        // granted for the run. Only a PATH directory qualifies: the operator put it there.
        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Users\dev\.cargo\bin\rg.exe",
            @"C:\Users\dev\tool.exe",
        };

        Result<WindowsBrokerTarget> onPath = WindowsBrokerTargetResolver.Resolve(
            "rg",
            SearchPath,
            PathExt,
            files.Contains,
            userProfile: UserProfile);

        Result<WindowsBrokerTarget> profileRoot = WindowsBrokerTargetResolver.Resolve(
            "tool",
            SearchPath + @";C:\Users\dev",
            PathExt,
            files.Contains,
            userProfile: UserProfile);

        Result<WindowsBrokerTarget> explicitPath = WindowsBrokerTargetResolver.Resolve(
            @"C:\Users\dev\.cargo\bin\rg.exe",
            SearchPath,
            PathExt,
            files.Contains,
            userProfile: UserProfile);

        Assert.True(onPath.IsSuccess);
        Assert.Equal(@"C:\Users\dev\.cargo\bin", onPath.Value.ReadExecuteRoot);

        // Never the whole profile, and never for a path the model named directly.
        Assert.True(profileRoot.IsSuccess);
        Assert.Null(profileRoot.Value.ReadExecuteRoot);
        Assert.True(explicitPath.IsSuccess);
        Assert.Null(explicitPath.Value.ReadExecuteRoot);
    }
}
