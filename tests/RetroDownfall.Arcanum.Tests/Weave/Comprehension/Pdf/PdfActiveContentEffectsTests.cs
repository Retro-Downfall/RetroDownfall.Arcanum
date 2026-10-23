using System.Net;

using System.Net.Sockets;

using System.Text;

using RetroDownfall.Arcanum.Infrastructure.Platform;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension;

using RetroDownfall.Arcanum.Infrastructure.Weave.Comprehension.Pdf;

using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Weave.Comprehension.Pdf;

/// <summary>
/// A PDF that carries a JavaScript action, a launch action, an embedded file, a URI, a form submission and a remote
/// go-to is read for its text and for nothing else. Each of those would leave a mark if it were run, followed or
/// extracted: a script that creates a file, a listener that receives a connection, a file written under the embedded
/// file's own name. The marks are looked for after the extraction rather than reasoned about, once in this process with
/// no jail at all (so a followed action would really happen) and once in the real jailed worker.
/// </summary>
[Collection("ChildProcess")]
public sealed class PdfActiveContentEffectsTests : IDisposable
{
    private readonly string _token = Guid.NewGuid().ToString("N");

    private readonly string _sentinels = Directory.CreateTempSubdirectory("arcanum-active-content-").FullName;

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public PdfActiveContentEffectsTests()
    {
        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Stop();

        Directory.Delete(_sentinels, recursive: true);
    }

    private int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private string VisibleText => $"ACTIVE-EFFECTS-VISIBLE-{_token}";

    private string LaunchScript => Path.Combine(_sentinels, $"launch-{_token}.sh");

    private string LaunchMark => Path.Combine(_sentinels, $"launched-{_token}");

    private string ScriptWrittenFile => Path.Combine(_sentinels, $"script-wrote-{_token}.txt");

    private string EmbeddedName => $"embedded-{_token}.txt";

    /// <summary>Every string the document carries that is not its visible text: none of them may reach the output.</summary>
    private string[] Payloads =>
    [
        $"JS-PAYLOAD-{_token}",
        $"LAUNCH-PAYLOAD-{_token}",
        $"EMBEDDED-PAYLOAD-{_token}",
        $"/uri-{_token}",
        $"/submit-{_token}",
        $"/remote-{_token}",
        $"FORM-VALUE-{_token}",
        EmbeddedName,
        LaunchScript,
        ScriptWrittenFile,
    ];

    [SkippableFact]
    public void The_extractor_runs_nothing_follows_nothing_and_emits_nothing()
    {
        Skip.If(OperatingSystem.IsWindows(), "The launch target here is a POSIX script.");

        byte[] document = BuildDocument();

        // No jail, no limits: an action the extractor followed would really happen in this process.
        DocumentExtractionOutcome outcome = new PdfTextExtractor().Extract(document, PdfExtractionLimits.Default, CancellationToken.None);

        Assert.Equal(DocumentExtractionKind.Extracted, outcome.Kind);

        Assert.Equal([VisibleText], outcome.Pages);

        AssertNothingEmitted(outcome.Pages);

        AssertNothingHappened();
    }

    [SkippableFact]
    public async Task The_jailed_worker_runs_nothing_follows_nothing_and_emits_nothing()
    {
        Skip.If(OperatingSystem.IsWindows(), "The launch target here is a POSIX script.");

        byte[] document = BuildDocument();

        using IDisposable launch = ManagedApphostDocumentWorker.Use();

        DocumentWorkerResult result = await new ProcessDocumentExtractionWorker(new ProcessResourceLimiter())
            .RunAsync(DocumentWorkerFormat.Pdf, document, CancellationToken.None);

        Assert.True(result.FailureReason is null, result.FailureReason);

        Assert.Equal(DocumentExtractionKind.Extracted, result.Outcome!.Kind);

        Assert.Equal([VisibleText], result.Outcome.Pages);

        AssertNothingEmitted(result.Outcome.Pages);

        AssertNothingHappened();
    }

    [SkippableFact]
    public void The_document_really_carries_every_action_it_is_supposed_to()
    {
        Skip.If(OperatingSystem.IsWindows(), "The launch target here is a POSIX script.");

        // Sensitivity of the two tests above: a document that had lost an action would pass them for the wrong reason.
        string source = Encoding.Latin1.GetString(BuildDocument());

        foreach (string expected in new[]
        {
            "/S /JavaScript", "/S /Launch", "/S /URI", "/S /SubmitForm", "/S /GoToR", "/EmbeddedFiles", "/Type /EmbeddedFile", "/OpenAction",
            $"JS-PAYLOAD-{_token}", $"LAUNCH-PAYLOAD-{_token}", $"EMBEDDED-PAYLOAD-{_token}", $"127.0.0.1:{Port}/uri-{_token}", LaunchScript,
        })
        {
            Assert.Contains(expected, source, StringComparison.Ordinal);
        }
    }

    private void AssertNothingEmitted(IReadOnlyList<string> pages)
    {
        string output = string.Concat(pages);

        foreach (string payload in Payloads)
        {
            Assert.DoesNotContain(payload, output, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("127.0.0.1", output, StringComparison.Ordinal);
    }

    private void AssertNothingHappened()
    {
        // A connection to a listening socket is queued by the kernel as soon as it is made; the pause is only for a late one.
        Thread.Sleep(300);

        Assert.False(_listener.Pending(), "A connection reached the listener: a URI, submission or remote action was followed.");

        // Nothing but the launch script this test wrote has appeared beside it.
        Assert.Equal([LaunchScript], Directory.GetFileSystemEntries(_sentinels));

        Assert.False(File.Exists(LaunchMark), "The launch action ran.");

        Assert.False(File.Exists(ScriptWrittenFile), "The script wrote a file.");

        foreach (string directory in new[] { Directory.GetCurrentDirectory(), Path.GetTempPath(), AppContext.BaseDirectory, _sentinels })
        {
            Assert.False(File.Exists(Path.Combine(directory, EmbeddedName)), $"The embedded file was written under {directory}.");
        }
    }

    private byte[] BuildDocument()
    {
        // If the launch action ran, the script would leave this mark; the script itself is part of the evidence.
        File.WriteAllText(LaunchScript, $"#!/bin/sh\n: > '{LaunchMark}'\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(LaunchScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        string script = $"app.alert('JS-PAYLOAD-{_token}'); this.saveAs('{ScriptWrittenFile}'); app.launchURL('http://127.0.0.1:{Port}/js-{_token}');";

        string javaScript = $"<< /S /JavaScript /JS ({script}) >>";

        string launch = $"<< /S /Launch /F ({LaunchScript}) /P (LAUNCH-PAYLOAD-{_token}) "
            + $"/Unix << /F ({LaunchScript}) >> /Mac << /F ({LaunchScript}) >> /Win << /F (cmd.exe) /P (/c LAUNCH-PAYLOAD-{_token}) >> >>";

        string uri = $"<< /S /URI /URI (http://127.0.0.1:{Port}/uri-{_token}) >>";

        string submit = $"<< /S /SubmitForm /F << /FS /URL /F (http://127.0.0.1:{Port}/submit-{_token}) >> /Flags 4 >>";

        string remote = $"<< /S /GoToR /F (http://127.0.0.1:{Port}/remote-{_token}.pdf) /D [0 /Fit] >>";

        return new PdfDocumentBuilder()
            .Object(1, $"<< /Type /Catalog /Pages 2 0 R /OpenAction {javaScript} /AcroForm 15 0 R /Names << /EmbeddedFiles 10 0 R /JavaScript 13 0 R >> >>")
            .Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Object(
                3,
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> "
                + $"/Annots [8 0 R 9 0 R 14 0 R 16 0 R 18 0 R 19 0 R] /AA << /O {javaScript} >> >>")
            .Stream(4, string.Empty, $"BT /F1 18 Tf 72 700 Td ({VisibleText}) Tj ET\n")
            .Object(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
            .Object(8, $"<< /Type /Annot /Subtype /Link /Rect [72 690 300 720] /A {launch} >>")
            .Object(9, $"<< /Type /Annot /Subtype /Link /Rect [72 650 300 680] /A {uri} >>")
            .Object(10, $"<< /Names [({EmbeddedName}) 11 0 R] >>")
            .Object(11, $"<< /Type /Filespec /F ({EmbeddedName}) /UF ({EmbeddedName}) /EF << /F 12 0 R >> >>")
            .Stream(12, "/Type /EmbeddedFile", $"EMBEDDED-PAYLOAD-{_token}")
            .Object(13, $"<< /Names [(init) {javaScript}] >>")
            .Object(14, $"<< /Type /Annot /Subtype /Link /Rect [72 610 300 640] /A {javaScript} >>")
            .Object(15, "<< /Fields [16 0 R] /DA (/Helv 0 Tf 0 g) >>")
            .Object(
                16,
                $"<< /Type /Annot /Subtype /Widget /FT /Btn /Ff 65536 /T (go) /V (FORM-VALUE-{_token}) /Rect [72 560 300 590] /P 3 0 R /A {submit} >>")
            .Object(18, $"<< /Type /Annot /Subtype /Link /Rect [72 520 300 550] /A {remote} >>")
            .Object(19, "<< /Type /Annot /Subtype /FileAttachment /Rect [72 480 90 500] /Name /Paperclip /FS 11 0 R >>")
            .Build();
    }
}
