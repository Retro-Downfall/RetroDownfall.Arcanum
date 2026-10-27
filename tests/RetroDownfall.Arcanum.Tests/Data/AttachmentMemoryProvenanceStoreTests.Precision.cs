using System.Globalization;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Data;

public sealed partial class AttachmentMemoryProvenanceStoreTests
{
    [SkippableTheory]
    [InlineData(0)]
    [InlineData(330)]
    [InlineData(-420)]
    public async Task Adjacent_windows_partition_consultations_at_every_tick_without_loss_or_leakage(int offsetMinutes)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid session = Guid.NewGuid();

        DateTimeOffset origin = DateTimeOffset.Parse("2026-10-26T12:00:00.0000000Z", CultureInfo.InvariantCulture);

        _db!.Sessions.Add(new Session
        {
            Id = session,
            Status = "active",
            CreatedAt = origin,
            UpdatedAt = origin,
        });

        Entry[] entries = Enumerable.Range(0, 5).Select(i => new Entry
        {
            Id = Guid.NewGuid(),
            SessionId = session,
            Role = MessageRole.Assistant,
            Content = "precision fixture",
            ModelUsed = "test-model",
            CreatedAt = origin.AddTicks(i),
            Sequence = i + 1,
        }).ToArray();

        _db.Entries.AddRange(entries);

        await _db.SaveChangesAsync();

        AttachmentMemoryProvenanceStore store = new(_db);

        Guid[] attachments = new Guid[entries.Length];

        for (int i = 0; i < entries.Length; i++)
        {
            attachments[i] = Guid.NewGuid();

            await store.RecordConsultationsAsync(entries[i].Id,
                [new AttachmentMemoryProvenance(session, attachments[i], "document", 1, "hash",
                    origin.AddSeconds(100 - i), "WorkspaceFile", AttachmentSourceAvailability.Available)],
                CancellationToken.None);
        }

        TimeSpan offset = TimeSpan.FromMinutes(offsetMinutes);

        IReadOnlyList<AttachmentMemoryProvenance> first = await store.ListConsultationsAsync(session,
            origin.AddTicks(-1).ToOffset(offset), origin.AddTicks(1).ToOffset(offset), CancellationToken.None);

        IReadOnlyList<AttachmentMemoryProvenance> middle = await store.ListConsultationsAsync(session,
            origin.AddTicks(1).ToOffset(offset), origin.AddTicks(3).ToOffset(offset), CancellationToken.None);

        IReadOnlyList<AttachmentMemoryProvenance> last = await store.ListConsultationsAsync(session,
            origin.AddTicks(3).ToOffset(offset), origin.AddTicks(4).ToOffset(offset), CancellationToken.None);

        Assert.Equal(attachments[..2], first.Select(item => item.AttachmentId));

        Assert.Equal(attachments[2..4], middle.Select(item => item.AttachmentId));

        Assert.Equal(attachments[4..], last.Select(item => item.AttachmentId));

        Assert.Equal(attachments, first.Concat(middle).Concat(last).Select(item => item.AttachmentId));

        Assert.All(first.Concat(middle).Concat(last), item => Assert.Equal(AttachmentSourceAvailability.Unavailable, item.Availability));
    }
}
