using System.ComponentModel;
using System.Diagnostics;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.ProcessExecution;

public sealed class TestDescendantProcessTests
{
    public static TheoryData<string> AlreadyGoneShapes => new()
    {
        nameof(IOException),
        nameof(FormatException),
        nameof(ArgumentException),
        nameof(InvalidOperationException),
        nameof(Win32Exception),
    };

    [Theory]
    [MemberData(nameof(AlreadyGoneShapes))]
    public void A_failure_that_means_there_was_nothing_left_to_kill_is_tolerated(string shape)
    {
        Exception exception = shape switch
        {
            nameof(IOException) => new IOException("pid file vanished"),
            nameof(FormatException) => new FormatException("pid file was empty"),
            nameof(ArgumentException) => new ArgumentException("no such process"),
            nameof(InvalidOperationException) => new InvalidOperationException("process already exited"),
            nameof(Win32Exception) => new Win32Exception("access denied on a dying process"),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown shape."),
        };

        Assert.True(TestDescendantProcess.IsDescendantAlreadyGone(exception));
    }

    [Fact]
    public void An_unrelated_failure_is_not_mistaken_for_a_descendant_that_is_already_gone()
    {
        Assert.False(TestDescendantProcess.IsDescendantAlreadyGone(new InvalidCastException("a real defect")));

        Assert.False(TestDescendantProcess.IsDescendantAlreadyGone(new NullReferenceException()));
    }

    [Fact]
    public void A_tree_kill_that_could_not_reach_every_member_is_reported_instead_of_masking_the_test_or_vanishing()
    {
        List<string> reports = [];

        TestDescendantProcess.KillTreeIfRunning(
            global::System.Environment.ProcessId,
            static _ => throw new AggregateException(new InvalidOperationException("one tree member refused to die")),
            reports.Add);

        string report = Assert.Single(reports);

        Assert.Contains(global::System.Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), report, StringComparison.Ordinal);

        Assert.Contains("one tree member refused to die", report, StringComparison.Ordinal);
    }

    [Fact]
    public void A_descendant_that_is_already_gone_is_tolerated_silently_and_a_real_failure_propagates()
    {
        List<string> reports = [];

        TestDescendantProcess.KillTreeIfRunning(
            global::System.Environment.ProcessId,
            static _ => throw new InvalidOperationException("The process has already exited."),
            reports.Add);

        Assert.Empty(reports);

        _ = Assert.Throws<InvalidCastException>(() => TestDescendantProcess.KillTreeIfRunning(
            global::System.Environment.ProcessId,
            static _ => throw new InvalidCastException("a real defect"),
            reports.Add));
    }

    [Fact]
    public void KillRecorded_tolerates_a_missing_empty_or_stale_pid_file()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"arcanum-descendant-{Guid.NewGuid():N}");

        Directory.CreateDirectory(directory);

        try
        {
            TestDescendantProcess.KillRecorded(Path.Combine(directory, "never-written.pid"));

            string empty = Path.Combine(directory, "empty.pid");

            File.WriteAllText(empty, string.Empty);

            TestDescendantProcess.KillRecorded(empty);

            string stale = Path.Combine(directory, "stale.pid");

            File.WriteAllText(stale, int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));

            TestDescendantProcess.KillRecorded(stale);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [SkippableFact]
    public void KillTreeIfRunning_kills_a_running_process_and_tolerates_it_being_gone_afterwards()
    {
        Skip.If(OperatingSystem.IsWindows(), "The child is /bin/sleep.");

        using global::System.Diagnostics.Process child = new();

        child.StartInfo = new ProcessStartInfo("/bin/sleep", "30")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        _ = child.Start();

        try
        {
            TestDescendantProcess.KillTreeIfRunning(child.Id);

            Assert.True(child.WaitForExit(TimeSpan.FromSeconds(10)), "The recorded descendant was not killed.");

            TestDescendantProcess.KillTreeIfRunning(child.Id);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
        }
    }
}
