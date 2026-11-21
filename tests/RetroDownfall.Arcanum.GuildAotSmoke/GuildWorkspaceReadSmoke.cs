using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Security;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Guild;
using RetroDownfall.Arcanum.Infrastructure.Tower;

internal static class GuildWorkspaceReadSmoke
{
    internal static async Task VerifyAsync(string root)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, "granted")).FullName;

        string literal = string.Concat(Enumerable.Repeat("é雪\r\n", 100)) + "final marker";

        await File.WriteAllTextAsync(Path.Combine(directory, "notes.txt"), literal);

        Guid workspace = Guid.NewGuid();

        PhysicalCampaignRootOpener opener = new(new FixtureKey());

        GuildWorkspaceRegistration registration = new(workspace, root, 1, 1, opener.IdentifyExact(root)!.Value);

        GuildWorkspaceAuthority authority = new(registration, opener, _ => Task.FromResult(Result<GuildWorkspaceRegistration>.Success(registration)));

        GuildGrant grant = new([new(workspace, "granted", GuildWorkspaceAccess.Read)], [], [],
            new(new(GuildBudgetMode.Unlimited), new(GuildBudgetMode.Unlimited)), [], null);

        byte[] key = RandomNumberGenerator.GetBytes(32);

        StringBuilder restored = new();

        string? continuation = null;

        int pages = 0;

        do
        {
            string token = continuation is null ? "null" : JsonSerializer.Serialize(continuation, ArcanumCoreJsonContext.Default.String);

            using JsonDocument args = JsonDocument.Parse(
                $$$"""{"relativePath":"granted/notes.txt","startLine":1,"endLine":101,"continuation":{{{token}}}}""");

            var result = await GuildWorkspaceReadTools.ExecuteAsync("read_file", args.RootElement, authority,
                grant, workspace, 512, new WorkspaceSearchSettings(), key, _ => Task.FromResult(Result.Success()), default);

            if (result.IsFailure) throw new InvalidOperationException(result.Error.Code);

            using JsonDocument page = JsonDocument.Parse(result.Value);

            string content = page.RootElement.GetProperty("content").GetString()!;

            if (content.Length == 0 || restored.Length + content.Length > literal.Length)
                throw new InvalidOperationException("The native workspace read did not advance within the selected file.");

            restored.Append(content);

            continuation = page.RootElement.GetProperty("continuation").GetString();

            pages++;
        }
        while (continuation is not null);

        if (pages < 2 || restored.ToString() != literal)
            throw new InvalidOperationException("The native workspace read lost selected bytes between pages.");
    }

    private sealed class FixtureKey : ICampaignRootIdentityKeyProvider
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

        public bool TryCopyRootIdentityKey(Span<byte> destination) => _key.AsSpan().TryCopyTo(destination);
    }
}
