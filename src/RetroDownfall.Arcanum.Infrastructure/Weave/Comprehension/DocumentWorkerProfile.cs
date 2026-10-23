using System.Diagnostics.CodeAnalysis;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Sanctum;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

namespace RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

/// <summary>
/// Everything one kind of isolated document worker runs and reads by, on both sides of the process boundary: the argument
/// that selects it, its two clocks, its code-owned process limits, its input and output bounds, the heading its pages are
/// projected under, and its code-owned reasons. The host launches and judges a run by it
/// (<see cref="ProcessDocumentExtractionWorker"/>), the re-launched worker resolves it from its argument and runs by it
/// (<see cref="ComprehensionWorkerEntry"/>), and the isolated host projects the verdict with it
/// (<see cref="SessionAttachmentIsolatedDocumentHost"/>). None of it is configuration. The jail is not part of a profile:
/// it is the same for every kind (<see cref="ProcessDocumentExtractionWorker.CreateSandbox"/>), it must be applied, and no
/// kind can opt out of it. A new kind adds a <see cref="DocumentWorkerFormat"/> value, its profile to <see cref="Find"/>,
/// and its extractor to <see cref="ComprehensionWorkerEntry.ExtractorFor"/>; a value without a profile is refused before
/// anything starts, and a worker argument without both is refused by the worker as a usage error.
/// </summary>
/// <param name="Format">The worker kind this profile is for.</param>
/// <param name="FormatArgument">
/// The worker mode's second argument, which names the kind to the re-launched binary. Distinct for every kind, and compared
/// ordinally.
/// </param>
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
/// <param name="InputCeilingBytes">
/// The most bytes the worker reads from stdin: one byte more proves the input too large and nothing is parsed. The host
/// starts no worker for more.
/// </param>
/// <param name="MaxOutputCharacters">
/// The output ceiling, in UTF-16 characters of a frame's body: the worker writes no frame over it and the host decodes
/// every frame against it.
/// </param>
/// <param name="HeadingOpening">
/// The opening of the heading the isolated host writes on a line of its own before each page with text, followed by a
/// space, the page number and <c>]</c>.
/// </param>
/// <param name="PasswordRequiredReason">The document needs a password, which is never guessed, stripped or asked for.</param>
/// <param name="MalformedReason">The bytes are not a document of this kind the parser can read.</param>
/// <param name="BoundsReason">The document declares or shows more than the extractor's bounds allow.</param>
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
    string HeadingOpening,
    string PasswordRequiredReason,
    string MalformedReason,
    string BoundsReason,
    string FailedReason,
    string DeadlineReason,
    string ResourceLimitsReason,
    string UnavailableReason)
{
    /// <summary>
    /// The PDF worker: argument <c>pdf</c>; a 10-second wall clock over the worker's own 8-second deadline; a 768 MB memory
    /// ceiling (the macOS footprint monitor, a Windows job object), 15 s of CPU, one process and 64 file descriptors; a 1 MiB
    /// file-size ceiling; a 2 MiB output cap, whose stdout half holds the largest frame the 262,144-character ceiling allows
    /// (three UTF-8 bytes per UTF-16 character, plus the header and trailer); the attachment intake ceiling as its input
    /// ceiling; and pages headed <c>[PDF page n]</c>.
    /// </summary>
    internal static DocumentWorkerProfile Pdf { get; } = new(
        Format: DocumentWorkerFormat.Pdf,
        FormatArgument: "pdf",
        WallClock: TimeSpan.FromSeconds(10),
        SelfDeadline: TimeSpan.FromSeconds(8),
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
        InputCeilingBytes: ArcanumRuntimeDefaults.CliMaxAttachFileSizeBytes,
        MaxOutputCharacters: PdfExtractionLimits.Default.MaxOutputCharacters,
        HeadingOpening: "[PDF page",
        PasswordRequiredReason: "Password-protected PDFs are not supported.",
        MalformedReason: "The PDF document is malformed.",
        BoundsReason: "The PDF document exceeds its extraction bounds.",
        FailedReason: "PDF extraction failed.",
        DeadlineReason: "PDF extraction exceeded its deadline.",
        ResourceLimitsReason: "PDF extraction exceeded its resource limits.",
        UnavailableReason: "PDF extraction is unavailable on this host.");

    /// <summary>
    /// What an occurrence of <see cref="HeadingOpening"/> in a page's own text is written as: the same text behind a
    /// backslash, so the document cannot print a line that reads as one of the host's headings.
    /// </summary>
    internal string EscapedHeadingOpening => "\\" + HeadingOpening;

    /// <summary>The profile of <paramref name="format"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The format has no profile, so no worker of it may run.</exception>
    internal static DocumentWorkerProfile For(DocumentWorkerFormat format) =>
        Find(format) ?? throw new ArgumentOutOfRangeException(nameof(format), format, "No worker profile exists for this format.");

    /// <summary>
    /// The profile whose <see cref="FormatArgument"/> is exactly <paramref name="formatArgument"/>, compared ordinally: how the
    /// re-launched worker learns which kind it is.
    /// </summary>
    internal static bool TryForArgument(string formatArgument, [NotNullWhen(true)] out DocumentWorkerProfile? profile)
    {
        foreach (DocumentWorkerFormat format in Enum.GetValues<DocumentWorkerFormat>())
        {
            if (Find(format) is { } candidate && string.Equals(candidate.FormatArgument, formatArgument, StringComparison.Ordinal))
            {
                profile = candidate;

                return true;
            }
        }

        profile = null;

        return false;
    }

    private static DocumentWorkerProfile? Find(DocumentWorkerFormat format) => format switch
    {
        DocumentWorkerFormat.Pdf => Pdf,
        _ => null,
    };
}
