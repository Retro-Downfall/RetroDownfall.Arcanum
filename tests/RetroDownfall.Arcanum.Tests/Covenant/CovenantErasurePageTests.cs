using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.DataLifecycle;

namespace RetroDownfall.Arcanum.Tests.Covenant;

/// <summary>
/// The bounded, immutable erasure page and the caller-supplied values it refuses to carry.
/// </summary>
public sealed class CovenantErasurePageTests
{

    [Fact]
    public void Page_requires_a_nonempty_generation_and_a_bounded_nonempty_item_set()
    {

        CovenantProtectedArtifactErasureItem item =
            CovenantErasureAuthorityFixture.Item(Guid.NewGuid(), Guid.NewGuid());

        _ = Assert.Throws<ArgumentException>(() =>
            new CovenantProtectedArtifactErasurePage(Guid.Empty, [item]));

        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CovenantProtectedArtifactErasurePage(CovenantOperationGateFixture.DatasetGeneration, []));

        IReadOnlyList<CovenantProtectedArtifactErasureItem> tooMany =
        [
            .. Enumerable
                .Range(0, CovenantProtectedArtifactErasurePage.MaxItems + 1)
                .Select(static _ => CovenantErasureAuthorityFixture.Item(Guid.NewGuid(), Guid.NewGuid())),
        ];

        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CovenantProtectedArtifactErasurePage(CovenantOperationGateFixture.DatasetGeneration, tooMany));

    }

    [Fact]
    public void Page_rejects_a_repeated_artifact_or_label()
    {

        Guid artifactId = Guid.NewGuid();

        Guid labelId = Guid.NewGuid();

        _ = Assert.Throws<ArgumentException>(() =>
            new CovenantProtectedArtifactErasurePage(
                CovenantOperationGateFixture.DatasetGeneration,
                [
                    CovenantErasureAuthorityFixture.Item(artifactId, labelId),
                    CovenantErasureAuthorityFixture.Item(artifactId, Guid.NewGuid()),
                ]));

        _ = Assert.Throws<ArgumentException>(() =>
            new CovenantProtectedArtifactErasurePage(
                CovenantOperationGateFixture.DatasetGeneration,
                [
                    CovenantErasureAuthorityFixture.Item(Guid.NewGuid(), labelId),
                    CovenantErasureAuthorityFixture.Item(Guid.NewGuid(), labelId),
                ]));

    }

    [Fact]
    public void Page_deep_copies_its_items_and_exposes_no_mutable_backing_collection()
    {

        List<CovenantProtectedArtifactErasureItem> supplied =
        [
            CovenantErasureAuthorityFixture.Item(Guid.NewGuid(), Guid.NewGuid()),
        ];

        CovenantProtectedArtifactErasurePage page = new(
            CovenantOperationGateFixture.DatasetGeneration,
            supplied);

        supplied.Add(CovenantErasureAuthorityFixture.Item(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Single(page.Items);

        Assert.IsNotType<List<CovenantProtectedArtifactErasureItem>>(page.Items);

    }

    [Fact]
    public void An_item_cannot_disagree_with_the_label_it_carries()
    {

        Guid artifactId = Guid.NewGuid();

        Guid labelId = Guid.NewGuid();

        Guid sessionId = Guid.NewGuid();

        ArtifactSensitivityLabel label = CovenantErasureAuthorityFixture.Label(
            artifactId,
            labelId,
            SensitiveArtifactKind.Summary,
            sessionId);

        // A different Session on the item than on the label is the exact shape of "this row belongs to
        // somebody else", and it must fail before the kernel ever opens a transaction.
        _ = Assert.Throws<ArgumentException>(() =>
            new CovenantProtectedArtifactErasureItem(
                artifactId,
                SensitiveArtifactKind.Summary,
                Guid.NewGuid(),
                labelId,
                label,
                CovenantOperationGateFixture.Digest(0x11),
                0));

        _ = Assert.Throws<ArgumentException>(() =>
            new CovenantProtectedArtifactErasureItem(
                artifactId,
                SensitiveArtifactKind.Lexicon,
                sessionId,
                labelId,
                label,
                CovenantOperationGateFixture.Digest(0x11),
                0));

        _ = Assert.Throws<ArgumentException>(() =>
            new CovenantProtectedArtifactErasureItem(
                artifactId,
                SensitiveArtifactKind.Summary,
                sessionId,
                labelId,
                label,
                CovenantOperationGateFixture.Digest(0x11),
                7));

    }

    [Fact]
    public void An_item_rejects_an_unknown_artifact_kind_and_an_empty_identity()
    {

        Guid artifactId = Guid.NewGuid();

        Guid labelId = Guid.NewGuid();

        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CovenantProtectedArtifactErasureItem(
                artifactId,
                (SensitiveArtifactKind)14,
                null,
                labelId,
                CovenantErasureAuthorityFixture.Label(artifactId, labelId),
                CovenantOperationGateFixture.Digest(0x11),
                0));

        _ = Assert.Throws<ArgumentException>(() =>
            new CovenantProtectedArtifactErasureItem(
                Guid.Empty,
                SensitiveArtifactKind.AssistantEntry,
                null,
                labelId,
                CovenantErasureAuthorityFixture.Label(artifactId, labelId),
                CovenantOperationGateFixture.Digest(0x11),
                0));

    }

    /// <summary>
    /// The request names durable identities only. Every location fact is copied from the producer's own
    /// row inside the kernel, so a caller cannot aim the deleter at a file Arcanum never created.
    /// </summary>
    [Fact]
    public void Managed_file_request_carries_no_location_ownership_or_scope_value()
    {

        IReadOnlyList<string> properties =
        [
            .. typeof(CovenantManagedFileErasureRequest)
                .GetProperties()
                .Select(static property => property.Name),
        ];

        Assert.Equal(
            [
                "ArtifactId",
                "ExpectedSourceWriteRevision",
                "OperationId",
                "SensitivityLabelId",
                "SourceManagedWriteOperationId",
                "WorkItemId",
            ],
            properties.OrderBy(static name => name, StringComparer.Ordinal));

        _ = Assert.Throws<ArgumentException>(() =>
            new CovenantManagedFileErasureRequest(
                Guid.Empty,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                0));

    }

}
