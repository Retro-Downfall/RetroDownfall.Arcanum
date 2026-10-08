using System.Runtime.InteropServices;

namespace RetroDownfall.Arcanum.Infrastructure.ProcessExecution;

internal sealed partial class MacOsDescendantSupervisor : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval =
        TimeSpan.FromMilliseconds(10);

    private readonly int _rootPid;

    private readonly ProcessIdentity _rootIdentity;

    private readonly int _kernelQueue;

    private readonly IntPtr _eventBuffer;
    private readonly object _gate = new();
    private readonly HashSet<ProcessIdentity> _tracked = [];
    private readonly CancellationTokenSource _monitorCts = new();
    private readonly Func<Task>? _monitorTickHold;

    private readonly long? _memoryLimitBytes;

    private readonly Func<int, long?> _footprintReader;

    private readonly Action? _processScanHook;

    private readonly DescendantScanSchedule _scanSchedule;

    private readonly HashSet<ProcessIdentity> _unreadableFootprints = [];

    private readonly Task _monitorTask;

    private volatile bool _memoryLimitExceeded;

    private volatile bool _watcherGap;

    private volatile bool _monitorFaulted;

    private Exception? _monitorFault;

    private long _fullScanCount;

    private long _monitorTickCount;

    private bool _stopped;

    private MacOsDescendantSupervisor(
        int rootPid,
        ProcessIdentity rootIdentity,
        int kernelQueue,
        IntPtr eventBuffer,
        Func<Task>? monitorTickHold,
        long? memoryLimitBytes,
        Func<int, long?>? footprintReader,
        int idleScanEveryTicks,
        Action? processScanHook)
    {
        _rootPid = rootPid;
        _rootIdentity = rootIdentity;
        _kernelQueue = kernelQueue;
        _eventBuffer = eventBuffer;
        _monitorTickHold = monitorTickHold;
        _memoryLimitBytes = memoryLimitBytes;
        _footprintReader = footprintReader ?? ReadPhysicalFootprintOrNull;
        _processScanHook = processScanHook;

        // Without a working kqueue nothing announces a fork, so the scan is the only detector and the
        // schedule must never back off.
        _scanSchedule = new DescendantScanSchedule(
            eventDriven: KernelEventsAvailable,
            idleScanEveryTicks);
        _tracked.Add(rootIdentity);
        _monitorTask = MonitorAsync();
    }

    /// <param name="rootPid">The directly started child.</param>
    /// <param name="monitorTickHold">
    /// Always <c>null</c> in production. A test supplies it to park the monitor loop inside a tick
    /// and prove that disposal waits for the loop to finish before releasing the kqueue buffer; the
    /// window is a scheduling race that no wall-clock test could reproduce reliably.
    /// </param>
    /// <param name="memoryLimitBytes">
    /// The Sanctum memory ceiling macOS cannot enforce in the kernel (it rejects RLIMIT_AS). When
    /// set, every monitor tick sums the physical footprint of the root and every tracked descendant
    /// and, once the sum exceeds the ceiling, records <see cref="MemoryLimitExceeded"/> and kills the
    /// root and the tracked tree. The supervisor is not returned when the root's footprint cannot be
    /// read, so the caller fails closed rather than running an unmonitored child.
    /// </param>
    /// <param name="footprintReader">
    /// Always <c>null</c> in production (the real <c>proc_pid_rusage</c> read). A test supplies it to make
    /// a descendant's footprint unreadable, which no real process does on demand.
    /// </param>
    /// <param name="idleScanEveryTicks">
    /// Always the schedule's default in production. A test stretches the quiescent safety cadence so far
    /// that a kqueue fork or exit event is the only thing that can resume scanning; at the default cadence
    /// the safety scan, and the window a newly found process reopens, would resume it without any event.
    /// </param>
    /// <param name="processScanHook">
    /// Always <c>null</c> in production. A test supplies it to make the full process-table scan itself
    /// throw, which no real process table does on demand; it runs at the start of every scan.
    /// </param>
    internal static MacOsDescendantSupervisor? TryStart(
        int rootPid,
        Func<Task>? monitorTickHold = null,
        long? memoryLimitBytes = null,
        Func<int, long?>? footprintReader = null,
        int idleScanEveryTicks = DescendantScanSchedule.IdleScanEveryTicks,
        Action? processScanHook = null)
    {
        Func<int, long?> readFootprint = footprintReader ?? ReadPhysicalFootprintOrNull;

        if (!OperatingSystem.IsMacOS()
            || !TryReadProcess(rootPid, out ProcessSnapshot root)
            || (memoryLimitBytes is not null
                && readFootprint(rootPid) is null))
        {
            return null;
        }

        int queue = Kqueue();
        IntPtr change = IntPtr.Zero;
        IntPtr events = IntPtr.Zero;

        try
        {
            if (queue >= 0)
            {
                change = Marshal.AllocHGlobal(
                    Marshal.SizeOf<KeventRecord>());
                KeventRecord registration = new()
                {
                    Ident = (nuint)rootPid,
                    Filter = -5,
                    Flags = 0x0001 | 0x0004 | 0x0020,
                    FilterFlags =
                        0x40000000u
                        | 0x80000000u,
                };
                Marshal.StructureToPtr(
                    registration,
                    change,
                    fDeleteOld: false);

                if (Kevent(
                        queue,
                        change,
                        1,
                        IntPtr.Zero,
                        0,
                        IntPtr.Zero) < 0)
                {
                    _ = Close(queue);
                    queue = -1;
                }
            }

            if (queue >= 0)
            {
                events = Marshal.AllocHGlobal(
                    Marshal.SizeOf<KeventRecord>() * 64);
            }

            return new MacOsDescendantSupervisor(
                rootPid,
                root.Identity,
                queue,
                events,
                monitorTickHold,
                memoryLimitBytes,
                footprintReader,
                idleScanEveryTicks,
                processScanHook);
        }
        catch
        {
            if (events != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(events);
            }

            if (queue >= 0)
            {
                _ = Close(queue);
            }

            return null;
        }
        finally
        {
            if (change != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(change);
            }
        }
    }

    /// <summary>
    /// Kills every tracked descendant whose identity still matches, after one last scan for descendants
    /// forked since the previous one. The scan is best effort and the kill does not depend on it: when the
    /// scan itself is what failed (a faulted monitor loop), rescanning first threw again and the
    /// descendants already tracked outlived the root with nothing enforcing the ceiling over them.
    /// </summary>
    internal void KillTracked()
    {
        _ = TryDiscoverDescendants();

        ProcessIdentity[] tracked;

        lock (_gate)
        {
            tracked = [.. _tracked];
        }

        foreach (ProcessIdentity identity in tracked)
        {
            if (identity.Pid == _rootPid)
            {
                continue;
            }

            KillIfIdentityMatches(identity);
        }
    }

    internal async Task<bool> StopKillAndVerifyAsync(
        TimeSpan timeout)
    {
        if (_stopped)
        {
            return VerifyTrackedExited();
        }

        _stopped = true;
        _monitorCts.Cancel();

        try
        {
            await _monitorTask.WaitAsync(
                    TimeSpan.FromSeconds(1))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A cancelled or slow loop is the expected outcome, and a faulted one has already recorded
            // itself (MonitorFault). None of them is the caller's problem: this method exists to stop and
            // verify the tree, and an exception here skipped the rest of the runner's teardown.
        }

        long deadline = Environment.TickCount64
            + (long)Math.Max(1, timeout.TotalMilliseconds);
        int quietScans = 0;

        while (Environment.TickCount64 < deadline)
        {
            int before = TrackedCount;
            _ = TryDiscoverDescendants();
            KillTracked();
            bool anyAlive = !VerifyTrackedExited();
            int after = TrackedCount;

            if (!anyAlive && before == after)
            {
                quietScans++;

                if (quietScans >= 3)
                {
                    return true;
                }
            }
            else
            {
                quietScans = 0;
            }

            await Task.Delay(PollInterval)
                .ConfigureAwait(false);
        }

        KillTracked();
        return VerifyTrackedExited();
    }

    public async ValueTask DisposeAsync()
    {
        _ = await StopKillAndVerifyAsync(
                TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);

        // The monitor loop hands _eventBuffer and _kernelQueue to kevent(2) on every tick, so both
        // must outlive the loop: freeing them first lets the kernel write records into freed heap.
        // StopKillAndVerifyAsync's own wait cannot stand in for this — it is bounded and swallows
        // its timeout, and the runner's earlier call has already set _stopped, so the call above
        // short-circuits and waits for nothing. The loop is cancelled and every syscall it makes is
        // bounded, so waiting for it to actually finish terminates.
        try
        {
            await _monitorTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _monitorCts.Dispose();
        if (_eventBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_eventBuffer);
        }

        if (_kernelQueue >= 0)
        {
            _ = Close(_kernelQueue);
        }
    }

    /// <summary>
    /// How many processes (the root included) the supervisor is tracking, live or already exited.
    /// </summary>
    internal int TrackedCount
    {
        get
        {
            lock (_gate)
            {
                return _tracked.Count;
            }
        }
    }

    private async Task MonitorAsync()
    {
        try
        {
            while (!_monitorCts.IsCancellationRequested)
            {
                _ = Interlocked.Increment(ref _monitorTickCount);

                bool kernelEventObserved = TrackKernelEvents();

                if (_watcherGap)
                {
                    _scanSchedule.RequireContinuousScanning();
                }

                // The full reconciliation is the only way to learn a descendant's identity: macOS
                // delivers NOTE_FORK without the child's pid, so the kqueue watcher can tell us that a
                // tracked process forked but never which pid to track, and the identity has to be learned
                // before that descendant escapes its process group and its parent exits (after
                // reparenting to launchd no ancestry walk can attribute it to this root). That is why the
                // scan is not throttled by time: it runs on every tick while anything could have changed —
                // a kernel event, or a scan that just found a new process — and backs off only once a full
                // window of scans has found nothing and every tracked process has a working watcher.
                if (_scanSchedule.ShouldScan(kernelEventObserved))
                {
                    _scanSchedule.RecordScan(DiscoverDescendants());
                }

                EnforceMemoryLimit();

                if (_monitorTickHold is not null)
                {
                    await _monitorTickHold().ConfigureAwait(false);
                }

                await Task.Delay(
                        PollInterval,
                        _monitorCts.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_monitorCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            FailClosedAfterMonitorFault(ex);
        }
    }

    /// <summary>
    /// The monitor loop stopped for a reason other than cancellation. Nothing awaits it until disposal, so
    /// an exception left on the task would surface there and skip the runner's teardown; it is recorded
    /// instead. When a memory ceiling is set this loop is the only thing enforcing it, so the tree is
    /// ended rather than left running unmonitored — the runner then reports the run as one whose memory
    /// monitor stopped.
    /// </summary>
    private void FailClosedAfterMonitorFault(Exception fault)
    {
        _monitorFault = fault;

        _monitorFaulted = true;

        if (_memoryLimitBytes is null)
        {
            return;
        }

        try
        {
            KillIfIdentityMatches(_rootIdentity);

            KillTracked();
        }
        catch (Exception)
        {
            // Best effort by definition: the loop that would have enforced the ceiling is already down.
        }
    }

    /// <returns>Whether the kernel reported a fork or exit of any watched process this tick.</returns>
    private bool TrackKernelEvents()
    {
        if (_kernelQueue < 0
            || _eventBuffer == IntPtr.Zero)
        {
            return false;
        }

        Timespec timeout = new()
        {
            Nanoseconds = 1_000_000,
        };
        IntPtr timeoutPointer = Marshal.AllocHGlobal(
            Marshal.SizeOf<Timespec>());

        try
        {
            Marshal.StructureToPtr(
                timeout,
                timeoutPointer,
                fDeleteOld: false);
            int count = Kevent(
                _kernelQueue,
                IntPtr.Zero,
                0,
                _eventBuffer,
                64,
                timeoutPointer);

            for (int index = 0;
                 index < count;
                 index++)
            {
                IntPtr current = IntPtr.Add(
                    _eventBuffer,
                    index * Marshal.SizeOf<KeventRecord>());
                KeventRecord processEvent =
                    Marshal.PtrToStructure<KeventRecord>(
                        current);
                int pid = checked((int)processEvent.Ident);

                if (pid > 0
                    && TryReadProcess(
                        pid,
                        out ProcessSnapshot process))
                {
                    lock (_gate)
                    {
                        _tracked.Add(process.Identity);
                    }
                }
            }

            return count > 0;
        }
        finally
        {
            Marshal.FreeHGlobal(timeoutPointer);
        }
    }

    /// <summary>
    /// Whether the kernel can announce a fork or exit of a watched process. Without it nothing but the scan
    /// can detect a descendant, so the scan never backs off (<see cref="DescendantScanSchedule"/>).
    /// </summary>
    internal bool KernelEventsAvailable =>
        _kernelQueue >= 0
        && _eventBuffer != IntPtr.Zero;

    /// <summary>
    /// True once the watcher of a live process could not be registered, so its forks can no longer be heard
    /// and the scan stops backing off for the rest of the run.
    /// </summary>
    internal bool WatcherGap => _watcherGap;

    /// <summary>
    /// Number of full process-table scans performed so far. Compared against
    /// <see cref="MonitorTickCount"/> so a test can prove how often the scan runs — on every tick while
    /// something could have changed, rarely once it is quiescent (<see cref="DescendantScanSchedule"/>) —
    /// without depending on wall-clock rate, which varies with host load and coverage instrumentation.
    /// </summary>
    internal long FullScanCount => Interlocked.Read(ref _fullScanCount);

    /// <summary>
    /// Number of monitor-loop iterations performed so far.
    /// </summary>
    internal long MonitorTickCount => Interlocked.Read(ref _monitorTickCount);

    /// <summary>
    /// True once the monitored tree's summed physical footprint exceeded the configured memory
    /// ceiling and the supervisor killed it. This is the authoritative evidence that a kill was the
    /// Sanctum memory limit rather than an unrelated signal.
    /// </summary>
    internal bool MemoryLimitExceeded => _memoryLimitExceeded;

    /// <summary>
    /// True once the monitor loop stopped on an exception rather than on cancellation. With a memory
    /// ceiling configured the tree was killed when that happened, because nothing was enforcing the
    /// ceiling any more; the runner reports such a run as <see cref="CappedChildProcessOutcome.MemoryMonitorStopped"/>
    /// unless a breach had already been recorded.
    /// </summary>
    internal bool MonitorFaulted => _monitorFaulted;

    /// <summary>The exception that stopped the monitor loop, when <see cref="MonitorFaulted"/>.</summary>
    internal Exception? MonitorFault => _monitorFault;

    /// <summary>
    /// How many distinct tracked processes could not have their physical footprint read while a memory
    /// ceiling was being enforced and were still the same live process afterwards (one that exited between
    /// the two reads is not a gap). Each is left out of the summed footprint, so a non-zero count means the
    /// ceiling was enforced over less than the whole tree.
    /// </summary>
    internal long UnreadableFootprintCount
    {
        get
        {
            lock (_gate)
            {
                return _unreadableFootprints.Count;
            }
        }
    }

    /// <summary>
    /// Sums the physical footprint (private resident plus compressed memory, the figure macOS itself
    /// uses for per-process memory limits) of the root and every tracked descendant whose identity
    /// still matches, and kills the whole tree once it exceeds the ceiling. Sampling runs on every
    /// monitor tick, so a child can overshoot for at most one tick before it is killed.
    /// </summary>
    private void EnforceMemoryLimit()
    {
        if (_memoryLimitBytes is not long limit
            || _memoryLimitExceeded)
        {
            return;
        }

        ProcessIdentity[] tracked;

        lock (_gate)
        {
            tracked = [.. _tracked];
        }

        long total = 0;

        foreach (ProcessIdentity identity in tracked)
        {
            if (!TryReadProcess(
                    identity.Pid,
                    out ProcessSnapshot current)
                || current.Identity != identity)
            {
                continue;
            }

            if (_footprintReader(identity.Pid) is long footprint)
            {
                total += footprint;

                continue;
            }

            // A process that exited after the identity read is gone, not unmeasured: only one that is still
            // the same live process is a hole in the ceiling.
            if (!TryReadProcess(
                    identity.Pid,
                    out ProcessSnapshot afterFailedRead)
                || afterFailedRead.Identity != identity)
            {
                continue;
            }

            // Still the same live process, but its footprint is unreadable: it is excluded from the sum.
            // Counted so the gap is visible instead of silently shrinking the ceiling's coverage.
            lock (_gate)
            {
                _ = _unreadableFootprints.Add(identity);
            }
        }

        if (total <= limit)
        {
            return;
        }

        _memoryLimitExceeded = true;
        KillIfIdentityMatches(_rootIdentity);
        KillTracked();
    }

    private static long? ReadPhysicalFootprintOrNull(int pid) =>
        TryReadPhysicalFootprint(pid, out long footprint)
            ? footprint
            : null;

    private static bool TryReadPhysicalFootprint(
        int pid,
        out long footprintBytes)
    {
        footprintBytes = 0;

        if (!OperatingSystem.IsMacOS()
            || pid <= 0)
        {
            return false;
        }

        RusageInfoV0 usage = default;

        if (ProcPidRusage(pid, 0, ref usage) != 0)
        {
            return false;
        }

        footprintBytes = (long)Math.Min(usage.PhysFootprint, long.MaxValue);
        return true;
    }

    /// <summary>
    /// The scan on the kill and verify paths: a failure there must cost only the newest descendants, never
    /// the kill of the ones already tracked or the caller's teardown.
    /// </summary>
    /// <returns>Whether the scan ran and added a process to the tracked set.</returns>
    private bool TryDiscoverDescendants()
    {
        try
        {
            return DiscoverDescendants();
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <returns>Whether the scan added a process to the tracked set.</returns>
    private bool DiscoverDescendants()
    {
        _ = Interlocked.Increment(ref _fullScanCount);

        _processScanHook?.Invoke();

        IReadOnlyList<ProcessSnapshot> processes =
            ReadAllProcesses();

        if (processes.Count == 0)
        {
            return false;
        }

        Dictionary<int, ProcessSnapshot> byPid =
            processes.ToDictionary(
                process => process.Identity.Pid);
        HashSet<ulong> ancestorUniqueIds;

        lock (_gate)
        {
            ancestorUniqueIds = _tracked
                .Where(identity =>
                    byPid.TryGetValue(
                        identity.Pid,
                        out ProcessSnapshot current)
                    && current.Identity == identity)
                .Select(identity => identity.UniqueId)
                .ToHashSet();
        }

        ancestorUniqueIds.Add(_rootIdentity.UniqueId);
        bool changed;
        bool anyAdded = false;

        do
        {
            changed = false;

            foreach (ProcessSnapshot process in processes)
            {
                if (ancestorUniqueIds.Contains(
                        process.Identity.UniqueId)
                    || !ancestorUniqueIds.Contains(
                        process.ParentUniqueId))
                {
                    continue;
                }

                ancestorUniqueIds.Add(
                    process.Identity.UniqueId);

                bool added;

                lock (_gate)
                {
                    added = _tracked.Add(process.Identity);
                }

                changed |= added;
                anyAdded |= added;

                RegisterProcessWatcher(
                    process.Identity.Pid);
            }
        }

        while (changed);

        return anyAdded;
    }

    private bool VerifyTrackedExited()
    {
        lock (_gate)
        {
            return _tracked
                .Where(identity => identity.Pid != _rootPid)
                .All(identity =>
                    !TryReadProcess(
                        identity.Pid,
                        out ProcessSnapshot current)
                    || current.Identity != identity);
        }
    }

    private static void KillIfIdentityMatches(
        ProcessIdentity identity)
    {
        if (TryReadProcess(
                identity.Pid,
                out ProcessSnapshot current)
            && current.Identity == identity)
        {
            _ = Kill(identity.Pid, 9);
        }
    }

    private static IReadOnlyList<ProcessSnapshot>
        ReadAllProcesses()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return [];
        }

        int count = ProcListAllPids(IntPtr.Zero, 0);

        if (count <= 0)
        {
            return [];
        }

        count = Math.Min(count + 256, 32_768);
        int bytes = checked(count * sizeof(int));
        IntPtr buffer = Marshal.AllocHGlobal(bytes);

        try
        {
            int returned = ProcListAllPids(buffer, bytes);

            if (returned <= 0)
            {
                return [];
            }

            int[] pids = new int[Math.Min(returned, count)];
            Marshal.Copy(buffer, pids, 0, pids.Length);
            List<ProcessSnapshot> snapshots = [];

            foreach (int pid in pids)
            {
                if (pid > 0
                    && TryReadProcess(
                        pid,
                        out ProcessSnapshot snapshot))
                {
                    snapshots.Add(snapshot);
                }
            }

            return snapshots;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryReadProcess(
        int pid,
        out ProcessSnapshot snapshot)
    {
        snapshot = default;

        if (!OperatingSystem.IsMacOS()
            || pid <= 0)
        {
            return false;
        }

        ProcBsdInfo info = default;
        int expected = Marshal.SizeOf<ProcBsdInfo>();
        int read = ProcPidInfo(
            pid,
            3,
            0,
            ref info,
            expected);

        if (read != expected
            || info.Pid != (uint)pid)
        {
            return false;
        }

        ProcUniqueIdentifierInfo unique = default;
        int uniqueSize =
            Marshal.SizeOf<ProcUniqueIdentifierInfo>();

        if (ProcPidUniqueInfo(
                pid,
                17,
                0,
                ref unique,
                uniqueSize) != uniqueSize
            || unique.UniqueId == 0)
        {
            return false;
        }

        snapshot = new ProcessSnapshot(
            new ProcessIdentity(
                pid,
                unique.UniqueId),
            unique.ParentUniqueId);
        return true;
    }

    private void RegisterProcessWatcher(int pid)
    {
        if (_kernelQueue < 0)
        {
            return;
        }

        IntPtr change = Marshal.AllocHGlobal(
            Marshal.SizeOf<KeventRecord>());

        try
        {
            KeventRecord registration = new()
            {
                Ident = (nuint)pid,
                Filter = -5,
                Flags = 0x0001 | 0x0004 | 0x0020,
                FilterFlags =
                    0x40000000u
                    | 0x80000000u,
            };
            Marshal.StructureToPtr(
                registration,
                change,
                fDeleteOld: false);

            if (Kevent(
                    _kernelQueue,
                    change,
                    1,
                    IntPtr.Zero,
                    0,
                    IntPtr.Zero) < 0
                && Marshal.GetLastPInvokeError() != NoSuchProcess)
            {
                // A live process whose forks cannot be heard: only the scan can see what it creates, so
                // the scan must stop backing off. (A process that already exited needs no watcher.)
                _watcherGap = true;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(change);
        }
    }

    [LibraryImport(
        "/usr/lib/libproc.dylib",
        EntryPoint = "proc_listallpids")]
    private static partial int ProcListAllPids(
        IntPtr buffer,
        int bufferSize);

    [LibraryImport(
        "/usr/lib/libproc.dylib",
        EntryPoint = "proc_pidinfo")]
    private static partial int ProcPidInfo(
        int pid,
        int flavor,
        ulong argument,
        ref ProcBsdInfo buffer,
        int bufferSize);

    [LibraryImport(
        "/usr/lib/libproc.dylib",
        EntryPoint = "proc_pidinfo")]
    private static partial int ProcPidUniqueInfo(
        int pid,
        int flavor,
        ulong argument,
        ref ProcUniqueIdentifierInfo buffer,
        int bufferSize);

    [LibraryImport(
        "/usr/lib/libproc.dylib",
        EntryPoint = "proc_pid_rusage")]
    private static partial int ProcPidRusage(
        int pid,
        int flavor,
        ref RusageInfoV0 buffer);

    [LibraryImport(
        "libc",
        EntryPoint = "kill",
        SetLastError = true)]
    private static partial int Kill(
        int pid,
        int signal);

    [LibraryImport("libc", EntryPoint = "kqueue")]
    private static partial int Kqueue();

    /// <summary><c>ESRCH</c>: the process to watch is already gone, which needs no watcher.</summary>
    private const int NoSuchProcess = 3;

    [LibraryImport("libc", EntryPoint = "kevent", SetLastError = true)]
    private static partial int Kevent(
        int queue,
        IntPtr changes,
        int changeCount,
        IntPtr events,
        int eventCount,
        IntPtr timeout);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int descriptor);

    private readonly record struct ProcessIdentity(
        int Pid,
        ulong UniqueId);

    private readonly record struct ProcessSnapshot(
        ProcessIdentity Identity,
        ulong ParentUniqueId);

    [StructLayout(LayoutKind.Explicit, Size = 136)]
    private struct ProcBsdInfo
    {
        [FieldOffset(12)]
        internal uint Pid;

        [FieldOffset(16)]
        internal uint ParentPid;

        [FieldOffset(120)]
        internal ulong StartSeconds;

        [FieldOffset(128)]
        internal ulong StartMicroseconds;
    }

    [StructLayout(LayoutKind.Explicit, Size = 56)]
    private struct ProcUniqueIdentifierInfo
    {
        [FieldOffset(16)]
        internal ulong UniqueId;

        [FieldOffset(24)]
        internal ulong ParentUniqueId;
    }

    /// <summary>
    /// <c>struct rusage_info_v0</c> (<c>RUSAGE_INFO_V0</c>): a 16-byte uuid followed by ten
    /// <c>uint64_t</c> fields; <c>ri_phys_footprint</c> is the eighth.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 96)]
    private struct RusageInfoV0
    {
        [FieldOffset(72)]
        internal ulong PhysFootprint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeventRecord
    {
        internal nuint Ident;

        internal short Filter;

        internal ushort Flags;

        internal uint FilterFlags;

        internal nint Data;

        internal IntPtr UserData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        internal long Seconds;

        internal long Nanoseconds;
    }
}
