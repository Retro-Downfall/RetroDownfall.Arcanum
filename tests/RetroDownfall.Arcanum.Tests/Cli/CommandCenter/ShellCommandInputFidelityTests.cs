using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.CommandCenter;
using RetroDownfall.Arcanum.Cli.Infrastructure;
using RetroDownfall.Arcanum.Cli.Services;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Cli.CommandCenter;

[Collection("WorkspacePathPolicy")]
public sealed class ShellCommandInputFidelityTests
{
    [Theory]
    [InlineData("/attach My  Notes.txt", "My  Notes.txt")]
    [InlineData("/attach My\t Notes.txt", "My\t Notes.txt")]
    [InlineData("/attach\tMy  Notes.txt", "My  Notes.txt")]
    [InlineData("/pin file docs/a  b.md", "docs/a  b.md")]
    [InlineData("/pin\tfile\tdocs/a\tb.md", "docs/a\tb.md")]
    public void File_path_arguments_preserve_internal_whitespace(string input, string expected)
    {
        ParsedShellCommand parsed = new ShellCommandParser().Parse(input);
        Assert.Equal(expected, input.StartsWith("/attach", StringComparison.Ordinal) ? parsed.Argument : parsed.SecondaryArgument);
    }

    [Theory]
    [InlineData("branch")]
    [InlineData("session")]
    [InlineData("ward")]
    [InlineData("branch invalid")]
    [InlineData("session invalid")]
    [InlineData("ward invalid")]
    public void Incomplete_registered_commands_report_their_usage(string command)
    {
        Assert.True(SlashCommandRegistry.TryResolve(command.Split(' ')[0], out SlashCommandDescriptor? descriptor));
        ParsedShellCommand parsed = new ShellCommandParser().Parse("/" + command);
        Assert.Equal(ShellCommandKind.Denied, parsed.Kind);
        Assert.Contains(descriptor!.Usage, parsed.DenialMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Did you mean", parsed.DenialMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_session_has_no_child_and_does_not_resume_an_unrelated_root()
    {
        await using Harness h = new();
        h.State.Sessions = [new(Guid.NewGuid(), "Unrelated root", "Active", DateTimeOffset.UnixEpoch, 0)];
        await h.Dispatch("/branch child");
        Assert.Null(h.State.SessionId);
        Assert.Empty(h.Handler.Requests);
        Assert.Contains("session", h.State.Log.RenderPlainText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Config_reports_the_local_profile_path_without_reading_or_writing_configuration()
    {
        await using Harness h = new();
        await h.Dispatch("/config");
        string text = h.State.Log.RenderPlainText();
        Assert.Contains(Harness.ConfigPath, text, StringComparison.Ordinal);
        Assert.Contains("local", text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(h.Handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paths_with_two_spaces_do_not_stage_or_pin_the_different_single_space_file(bool pin)
    {
        await using Harness h = new();
        byte[] wanted = "requested contents"u8.ToArray();
        await File.WriteAllBytesAsync(Path.Combine(h.Root, "a  b.txt"), wanted);
        await File.WriteAllTextAsync(Path.Combine(h.Root, "a b.txt"), "different contents");
        h.State.ApplySessionMeta(Guid.NewGuid(), "Current", "Active", 0);
        await h.Dispatch(pin ? "/pin file a  b.txt" : "/attach a  b.txt");
        if (pin)
        {
            CreateSessionContextPinRequest request = Assert.Single(h.Handler.Pins);
            Assert.Equal("a  b.txt", request.TargetIdentifier);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(wanted)).ToLowerInvariant(), request.ContentVersion);
        }
        else
        {
            Assert.Equal(Path.Combine(h.Root, "a  b.txt"), Assert.Single(h.State.StagedAttachmentPaths));
        }
    }

    [SkippableFact]
    public async Task A_contained_file_opened_through_a_directory_alias_uses_its_kernel_relative_path()
    {
        await using Harness h = new();
        string alias = Path.Combine(h.Parent, "alias");
        CreateSymbolicLinkOrSkip(() => Directory.CreateSymbolicLink(alias, h.Root));
        await File.WriteAllTextAsync(Path.Combine(h.Root, "note.txt"), "content");
        h.State.ApplySessionMeta(Guid.NewGuid(), "Current", "Active", 0);
        await h.Dispatch("/pin file " + Path.Combine(alias, "note.txt"));
        Assert.Equal("note.txt", Assert.Single(h.Handler.Pins).TargetIdentifier);
    }

    [SkippableFact]
    public async Task An_existing_alternative_case_path_is_the_same_contained_file()
    {
        await using Harness h = new();
        string path = Path.Combine(h.Root, "CaseNote.txt");
        await File.WriteAllTextAsync(path, "content");
        string alternate = path.ToUpperInvariant();
        Skip.IfNot(File.Exists(alternate), "The fixture volume distinguishes case; an alternative-case name is not this file.");
        h.State.ApplySessionMeta(Guid.NewGuid(), "Current", "Active", 0);
        await h.Dispatch("/pin file " + alternate);
        Assert.Equal("CaseNote.txt", Assert.Single(h.Handler.Pins).TargetIdentifier);
    }

    [SkippableFact]
    public async Task A_link_inside_the_workspace_cannot_pin_an_outside_file()
    {
        await using Harness h = new();
        string outside = Path.Combine(h.Parent, "outside.txt");
        await File.WriteAllTextAsync(outside, "outside contents");
        CreateSymbolicLinkOrSkip(() => File.CreateSymbolicLink(Path.Combine(h.Root, "escape.txt"), outside));
        h.State.ApplySessionMeta(Guid.NewGuid(), "Current", "Active", 0);
        await h.Dispatch("/pin file escape.txt");
        Assert.Empty(h.Handler.Pins);
        Assert.Empty(h.Handler.Requests);
    }

    [Fact]
    public async Task Invalid_file_path_is_a_normal_refusal_without_an_escaped_exception()
    {
        await using Harness h = new();
        h.State.ApplySessionMeta(Guid.NewGuid(), "Current", "Active", 0);
        Exception? escaped = await Record.ExceptionAsync(() => h.Dispatch("/pin file bad\0path"));
        Assert.Null(escaped);
        Assert.Empty(h.Handler.Requests);
        Assert.Equal(SessionLogEntryKind.Error, h.State.Log.Snapshot()[^1].Kind);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("directory")]
    [InlineData("sibling")]
    public async Task Missing_files_directories_and_sibling_workspaces_remain_refused(string kind)
    {
        await using Harness h = new();
        string target = Path.Combine(h.Root, "missing.txt");
        if (kind == "directory")
        {
            target = Directory.CreateDirectory(Path.Combine(h.Root, "directory")).FullName;
        }
        else if (kind == "sibling")
        {
            target = Path.Combine(Directory.CreateDirectory(h.Root + "-sibling").FullName, "outside.txt");
            await File.WriteAllTextAsync(target, "outside contents");
        }
        h.State.ApplySessionMeta(Guid.NewGuid(), "Current", "Active", 0);
        await h.Dispatch("/pin file " + target);
        Assert.Empty(h.Handler.Requests);
        Assert.Equal(SessionLogEntryKind.Error, h.State.Log.Snapshot()[^1].Kind);
    }

    private static void CreateSymbolicLinkOrSkip(Action createLink)
    {
        try
        {
            createLink();
        }
        catch (IOException exception) when (OperatingSystem.IsWindows()
            && exception.HResult == unchecked((int)0x80070522))
        {
            // ERROR_PRIVILEGE_NOT_HELD: preserve assertions on capable Windows accounts while
            // treating this specific fixture prerequisite as unavailable elsewhere on Windows.
            Skip.If(true, "This Windows account lacks symbolic-link creation privilege or Developer Mode.");
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        internal const string ConfigPath = "/private/test-profile/arcanum.json";
        private readonly ServiceProvider _services;
        internal string Parent { get; } = Directory.CreateTempSubdirectory("arcanum-shell-fidelity-").FullName;
        internal string Root { get; }
        internal RecordingHandler Handler { get; } = new();
        internal CommandCenterState State { get; } = new(new SessionLogBuffer());
        internal ShellCommandDispatcher Dispatcher { get; }

        internal Harness()
        {
            Root = Directory.CreateDirectory(Path.Combine(Parent, "workspace")).FullName;
            State.WorkingDirectory = Root;
            ServiceCollection services = new();
            CliApplicationFactory.ConfigureCliServices(services, new ConfigurationManager());
            services.RemoveAll<ArcanumApiClient>();
            services.AddSingleton(new ArcanumApiClient(new Factory(Handler), ArcanumApiCredentialLeaseTestFactory.Create("test-key")));
            services.RemoveAll<IConfigurationCommandService>();
            services.AddSingleton<IConfigurationCommandService>(new CommandCenterConfigurationStub(ConfigPath));
            _services = services.BuildServiceProvider();
            Dispatcher = _services.GetRequiredService<ShellCommandDispatcher>();
        }

        internal Task Dispatch(string command) => Dispatcher.DispatchAsync(command, State, CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            Handler.Dispose();
            Assert.True(TestDirectoryCleanup.TryDelete(Parent, nameof(ShellCommandInputFidelityTests)));
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];
        internal List<CreateSessionContextPinRequest> Pins { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/context-pins", StringComparison.Ordinal))
            {
                CreateSessionContextPinRequest pin = JsonSerializer.Deserialize(await request.Content!.ReadAsByteArrayAsync(token),
                    ArcanumJsonContext.Default.CreateSessionContextPinRequest)!;
                Pins.Add(pin);
                SessionContextPinDto row = new(Guid.NewGuid(), Guid.Parse(request.RequestUri.AbsolutePath.Split('/')[3]), pin.Kind,
                    pin.TargetIdentifier, pin.DisplayLabel ?? pin.TargetIdentifier, pin.ContentVersion, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
                return new(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new ApiResponse<SessionContextPinDto>(row, true, null),
                        ArcanumJsonContext.Default.ApiResponseSessionContextPinDto)),
                };
            }

            return new(HttpStatusCode.NotFound);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost:5001/") };
    }
}
