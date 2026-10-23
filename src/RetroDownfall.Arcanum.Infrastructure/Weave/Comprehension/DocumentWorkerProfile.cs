using RetroDownfall.Arcanum.Core.Sanctum;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// What one kind of isolated document worker runs with: the argument that selects it, its two clocks, its code-owned
/// process limits and output bounds, and the four code-owned reasons a run without a verdict ends with. None of it is
/// configuration. The jail is not part of a profile: it is the same for every kind
/// (<see cref="ProcessDocumentExtractionWorker.CreateSandbox"/>), it must be applied, and no kind can opt out of it. A new
/// kind adds a <see cref="DocumentWorkerFormat"/> value and its profile to <see cref="For"/>; a value without one is refused
/// before anything starts.
/// </summary>
/// <param name="Format">The worker kind this profile is for.</param>
/// <param name="FormatArgument">The worker mode's second argument, which names the kind to the re-launched binary.</param>
/// <param name="WallClock">The host's clock from before the child starts until it is gone, cold start included.</param>
/// <param name="SelfDeadline">
/// The worker's own deadline over reading and parsing, under <paramref name="WallClock"/>, so the worker ends on its own
/// before the host has to kill it.
/// </param>
/// <param name="Limits">
/// The process limits: memory, CPU, process count and file descriptors. <c>MaxFileWriteMb</c> is Sanctum's write-tool
/// budget and stays 0; the kernel's ceiling on a file the worker writes is <paramref name="MaxFileSizeBytes"/>.
/// </param>
/// <param name="MaxFileSizeBytes">
/// The kernel ceiling on any one file the worker writes. The jail grants it nowhere to write; the ceiling stays as defence
/// in depth should a write ever be granted, though it bounds each file and not how many. Not enforced on Windows, which has
/// no per-process file-size limit.
/// </param>
/// <param name="TotalOutputCapBytes">
/// The runner's total output cap. Each stream gets half, and stdout must hold the largest frame
/// <paramref name="MaxOutputCharacters"/> allows.
/// </param>
/// <param name="InputCeilingBytes">The most bytes the worker reads from stdin; the host starts no worker for more.</param>
/// <param name="MaxOutputCharacters">The output ceiling a frame of this kind is decoded against.</param>
/// <param name="FailedReason">The worker gave no usable frame: it crashed, exited with a code of its own, or wrote something else.</param>
/// <param name="DeadlineReason">The worker outlived the wall clock, or gave up at its own deadline first.</param>
/// <param name="ResourceLimitsReason">The worker crossed its memory or CPU ceiling, or the memory monitor that enforces it stopped.</param>
/// <param name="UnavailableReason">
/// This host cannot run the worker: not a Native AOT binary (a dev host or a managed publish), no re-launchable binary, no
/// applied jail, no limits, or a replaced binary.
/// </param>
internal sealed record DocumentWorkerProfile(
    DocumentWorkerFormat Format,
    string FormatArgument,
    TimeSpan WallClock,
    TimeSpan SelfDeadline,
    ResourceLimits Limits,
    long MaxFileSizeBytes,
    long TotalOutputCapBytes,
    long InputCeilingBytes,
    int MaxOutputCharacters,
    string FailedReason,
    string DeadlineReason,
    string ResourceLimitsReason,
    string UnavailableReason)
{
    /// <summary>
    /// The PDF worker: a 10-second wall clock over the worker's own 8-second deadline; a 768 MB memory ceiling (the macOS
    /// footprint monitor, a Windows job object), 15 s of CPU, one process and 64 file descriptors; a 1 MiB file-size ceiling;
    /// a 2 MiB output cap, whose stdout half holds the largest frame the 262,144-character ceiling allows (three UTF-8 bytes
    /// per UTF-16 character, plus the header and trailer); and the attachment intake ceiling as its input ceiling.
    /// </summary>
    internal static DocumentWorkerProfile Pdf { get; } = new(
        Format: DocumentWorkerFormat.Pdf,
        FormatArgument: ComprehensionWorkerEntry.PdfFormatArgument,
        WallClock: TimeSpan.FromSeconds(10),
        SelfDeadline: ComprehensionWorkerEntry.SelfDeadline,
        Limits: new ResourceLimits(
            MaxProcessMemoryMb: 768,
            MaxProcessCount: 1,
            MaxFileWriteMb: 0,
            ProcessTimeoutSeconds: 10,
            MaxCpuSeconds: 15,
            MaxMemoryMb: 768,
            MaxFileDescriptors: 64),
        MaxFileSizeBytes: 1024 * 1024,
        TotalOutputCapBytes: 2L * 1024 * 1024,
        InputCeilingBytes: ComprehensionWorkerEntry.InputCeilingBytes,
        MaxOutputCharacters: PdfExtractionLimits.Default.MaxOutputCharacters,
        FailedReason: "PDF extraction failed.",
        DeadlineReason: "PDF extraction exceeded its deadline.",
        ResourceLimitsReason: "PDF extraction exceeded its resource limits.",
        UnavailableReason: "PDF extraction is unavailable on this host.");

    /// <summary>The profile of <paramref name="format"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The format has no profile, so no worker of it may run.</exception>
    internal static DocumentWorkerProfile For(DocumentWorkerFormat format) => format switch
    {
        DocumentWorkerFormat.Pdf => Pdf,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "No worker profile exists for this format."),
    };
}
