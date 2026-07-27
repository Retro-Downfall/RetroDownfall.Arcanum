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
    private readonly Task _monitorTask;
    private bool _stopped;

    private MacOsDescendantSupervisor(
        int rootPid,
        ProcessIdentity rootIdentity,
        int kernelQueue,
        IntPtr eventBuffer)
    {
        _rootPid = rootPid;
        _rootIdentity = rootIdentity;
        _kernelQueue = kernelQueue;
        _eventBuffer = eventBuffer;
        _tracked.Add(rootIdentity);
        _monitorTask = MonitorAsync();
    }

    internal static MacOsDescendantSupervisor? TryStart(
        int rootPid)
    {
        if (!OperatingSystem.IsMacOS()
            || !TryReadProcess(rootPid, out ProcessSnapshot root))
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
                events);
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

    internal void KillTracked()
    {
        DiscoverDescendants();

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
        catch (Exception ex) when (
            ex is OperationCanceledException
                or TimeoutException)
        {
        }

        long deadline = Environment.TickCount64
            + (long)Math.Max(1, timeout.TotalMilliseconds);
        int quietScans = 0;

        while (Environment.TickCount64 < deadline)
        {
            int before = TrackedCount;
            DiscoverDescendants();
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

    private int TrackedCount
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
                TrackKernelEvents();
                DiscoverDescendants();
                await Task.Delay(
                        PollInterval,
                        _monitorCts.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void TrackKernelEvents()
    {
        if (_kernelQueue < 0
            || _eventBuffer == IntPtr.Zero)
        {
            return;
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
        }
        finally
        {
            Marshal.FreeHGlobal(timeoutPointer);
        }
    }

    private void DiscoverDescendants()
    {
        IReadOnlyList<ProcessSnapshot> processes =
            ReadAllProcesses();

        if (processes.Count == 0)
        {
            return;
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

                lock (_gate)
                {
                    changed |= _tracked.Add(process.Identity);
                }

                RegisterProcessWatcher(
                    process.Identity.Pid);
            }
        }
        while (changed);
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
            _ = Kevent(
                _kernelQueue,
                change,
                1,
                IntPtr.Zero,
                0,
                IntPtr.Zero);
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
        "libc",
        EntryPoint = "kill",
        SetLastError = true)]
    private static partial int Kill(
        int pid,
        int signal);

    [LibraryImport("libc", EntryPoint = "kqueue")]
    private static partial int Kqueue();

    [LibraryImport("libc", EntryPoint = "kevent")]
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
