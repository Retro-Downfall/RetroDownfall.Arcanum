using RetroDownfall.Arcanum.Core.Backup;

using RetroDownfall.Arcanum.Infrastructure.Backup;

namespace RetroDownfall.Arcanum.Tests.Backup;

public sealed class BackupManifestValidationTests
{

    private static readonly byte[] Salt =
        Convert.FromHexString("000102030405060708090A0B0C0D0E0F");

    private static readonly DateTimeOffset CreatedAt =
        DateTimeOffset.FromUnixTimeMilliseconds(1_785_470_400_123);

    [Fact]
    public void ValidateManifest_AcceptsMatchingAuthenticatedHeaderMetadata()
    {

        BackupManifest manifest = Manifest();

        BackupArchiveCodec.ValidateManifest(
            manifest,
            Header(),
            Salt);

    }

    [Theory]
    [InlineData(BackupScope.Full)]
    [InlineData(BackupScope.SpecificSession)]
    public void ValidateManifest_AcceptsSessionProvenanceForBroaderAndSpecificScopes(
        BackupScope scope)
    {

        BackupManifest manifest = Manifest() with
        {

            Scope = scope,

            SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111"),

        };

        BackupArchiveCodec.ValidateManifest(
            manifest,
            Header(),
            Salt);

    }

    [Theory]
    [MemberData(nameof(HeaderMismatchCases))]
    public void ValidateManifest_RejectsEnvelopeOrTimestampThatDisagreesWithHeader(
        Func<BackupManifest, BackupManifest> mutate)
    {

        BackupManifest manifest = mutate(Manifest());

        Assert.Throws<InvalidDataException>(() =>
            BackupArchiveCodec.ValidateManifest(
                manifest,
                Header(),
                Salt));

    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ValidateManifest_RejectsUndefinedRequestedComponentValues(bool mutateIncludes)
    {

        BackupManifest manifest = mutateIncludes
            ? Manifest() with
            {

                RequestedIncludes = [(BackupComponent)int.MaxValue],

            }
            : Manifest() with
            {

                RequestedExcludes = [(BackupComponent)int.MaxValue],

            };

        Assert.Throws<InvalidDataException>(() =>
            BackupArchiveCodec.ValidateManifest(
                manifest,
                Header(),
                Salt));

    }

    [Theory]
    [MemberData(nameof(MalformedManifestCases))]
    public void ValidateManifest_RejectsMalformedReferenceMetadataAsInvalidData(
        Func<BackupManifest, BackupManifest> mutate)
    {

        BackupManifest manifest = mutate(Manifest());

        Assert.Throws<InvalidDataException>(() =>
            BackupArchiveCodec.ValidateManifest(
                manifest,
                Header(),
                Salt));

    }

    [Theory]
    [MemberData(nameof(SemanticMismatchCases))]
    public void ValidateManifest_RejectsNoncanonicalOrInconsistentSemanticMetadata(
        Func<BackupManifest, BackupManifest> mutate)
    {

        BackupManifest manifest = mutate(Manifest());

        Assert.Throws<InvalidDataException>(() =>
            BackupArchiveCodec.ValidateManifest(
                manifest,
                Header(),
                Salt));

    }

    public static TheoryData<Func<BackupManifest, BackupManifest>> HeaderMismatchCases()
    {

        TheoryData<Func<BackupManifest, BackupManifest>> cases = [];

        cases.Add(manifest => manifest with
        {

            CreatedAt = manifest.CreatedAt.AddMilliseconds(1),

        });

        cases.Add(manifest => WithEnvelope(
            manifest,
            envelope => envelope with { Kdf = "scrypt" }));

        cases.Add(manifest => WithEnvelope(
            manifest,
            envelope => envelope with { Prf = "HMAC-SHA512" }));

        cases.Add(manifest => WithEnvelope(
            manifest,
            envelope => envelope with
            {

                KdfIterations = envelope.KdfIterations + 1,

            }));

        cases.Add(manifest => WithEnvelope(
            manifest,
            envelope => envelope with { SaltBase64 = Convert.ToBase64String(new byte[16]) }));

        cases.Add(manifest => WithEnvelope(
            manifest,
            envelope => envelope with { Encryption = "AES-128-GCM" }));

        cases.Add(manifest => WithEnvelope(
            manifest,
            envelope => envelope with { KeyBits = 128 }));

        cases.Add(manifest => WithEnvelope(
            manifest,
            envelope => envelope with { NonceBytes = 16 }));

        cases.Add(manifest => WithEnvelope(
            manifest,
            envelope => envelope with { TagBytes = 12 }));

        cases.Add(manifest => WithEnvelope(
            manifest,
            envelope => envelope with { ChunkSize = envelope.ChunkSize * 2 }));

        return cases;

    }

    public static TheoryData<Func<BackupManifest, BackupManifest>> MalformedManifestCases()
    {

        TheoryData<Func<BackupManifest, BackupManifest>> cases = [];

        cases.Add(manifest => manifest with
        {

            Envelope = null!,

        });

        cases.Add(manifest => manifest with
        {

            Components = [null!],

        });

        cases.Add(manifest => manifest with
        {

            Entries = [null!],

        });

        cases.Add(manifest => manifest with
        {

            Entries =
            [
                new BackupManifestEntry(
                    Path: null!,
                    Size: 0,
                    Sha256: new string('0', 64),
                    BackupComponent.Configuration),
            ],

        });

        cases.Add(manifest => manifest with
        {

            Entries =
            [
                new BackupManifestEntry(
                    "configuration/arcanum.json",
                    Size: 0,
                    Sha256: null!,
                    BackupComponent.Configuration),
            ],

        });

        return cases;

    }

    public static TheoryData<Func<BackupManifest, BackupManifest>> SemanticMismatchCases()
    {

        TheoryData<Func<BackupManifest, BackupManifest>> cases = [];

        cases.Add(manifest => manifest with
        {

            ArcanumVersion = string.Empty,

        });

        cases.Add(manifest => manifest with
        {

            Build = " ",

        });

        cases.Add(manifest => manifest with
        {

            DatabaseSchemaVersion = string.Empty,

        });

        cases.Add(manifest => manifest with
        {

            Platform = string.Empty,

        });

        cases.Add(manifest => manifest with
        {

            Build = "build-e\u0301",

        });

        cases.Add(manifest => manifest with
        {

            Platform = new string('p', 4097),

        });

        cases.Add(manifest => manifest with
        {

            Scope = BackupScope.SpecificSession,

            SessionId = null,

        });

        cases.Add(manifest => manifest with
        {

            RequestedIncludes = [
                BackupComponent.CompendiumSettings,
                BackupComponent.Configuration,
            ],

        });

        cases.Add(manifest => manifest with
        {

            RequestedIncludes = [
                BackupComponent.Configuration,
                BackupComponent.Configuration,
            ],

        });

        cases.Add(manifest => manifest with
        {

            RequestedExcludes = [
                BackupComponent.MasterApiKey,
                BackupComponent.AuditLogs,
            ],

        });

        cases.Add(manifest => manifest with
        {

            RequestedExcludes = [
                BackupComponent.AuditLogs,
                BackupComponent.AuditLogs,
            ],

        });

        cases.Add(manifest => manifest with
        {

            Components = [.. manifest.Components.Reverse()],

        });

        cases.Add(manifest => manifest with
        {

            Components = [.. manifest.Components[..^1]],

        });

        cases.Add(manifest => manifest with
        {

            Components = [.. manifest.Components, manifest.Components[^1]],

        });

        cases.Add(manifest => ReplaceComponent(
            manifest,
            BackupComponent.Configuration,
            component => component with { Files = -1 }));

        cases.Add(manifest => ReplaceComponent(
            manifest,
            BackupComponent.Configuration,
            component => component with { Bytes = -1 }));

        cases.Add(manifest => ReplaceComponent(
            manifest,
            BackupComponent.AuditLogs,
            component => component with { Status = BackupComponentStatus.Failed }));

        cases.Add(manifest => ReplaceComponent(
            manifest,
            BackupComponent.AuditLogs,
            component => component with { Files = 1 }));

        cases.Add(manifest => ReplaceComponent(
            manifest,
            BackupComponent.AuditLogs,
            component => component with
            {

                Status = BackupComponentStatus.Unavailable,

                Bytes = 1,

            }));

        cases.Add(manifest => manifest with
        {

            Entries =
            [
                manifest.Entries[0] with
                {

                    Component = BackupComponent.AuditLogs,

                },
            ],

        });

        cases.Add(manifest => ReplaceComponent(
            manifest,
            BackupComponent.Configuration,
            component => component with { Files = component.Files + 1 }));

        cases.Add(manifest => ReplaceComponent(
            manifest,
            BackupComponent.Configuration,
            component => component with { Bytes = component.Bytes + 1 }));

        cases.Add(manifest => manifest with
        {

            Entries =
            [
                manifest.Entries[0] with
                {

                    Sha256 = new string('A', 64),

                },
            ],

        });

        cases.Add(manifest => manifest with
        {

            Entries =
            [
                manifest.Entries[0] with
                {

                    Sha256 = new string('g', 64),

                },
            ],

        });

        cases.Add(manifest => manifest with
        {

            Entries =
            [
                manifest.Entries[0] with
                {

                    Sha256 = new string('0', 63),

                },
            ],

        });

        cases.Add(manifest =>
        {

            BackupManifestEntry duplicate = manifest.Entries[0];

            BackupManifestEntry[] entries = [duplicate, duplicate];

            return manifest with
            {

                Components = ComponentsFor(entries),

                Entries = entries,

            };

        });

        cases.Add(manifest =>
        {

            BackupManifestEntry authored = Entry(
                "authored/CODEX.md",
                size: 3,
                BackupComponent.GlobalCodex);

            BackupManifestEntry[] entries = [manifest.Entries[0], authored];

            return manifest with
            {

                Components = ComponentsFor(entries),

                Entries = entries,

            };

        });

        return cases;

    }

    private static BackupManifest WithEnvelope(
        BackupManifest manifest,
        Func<BackupEnvelopeDescriptor, BackupEnvelopeDescriptor> mutate) =>
        manifest with
        {

            Envelope = mutate(manifest.Envelope),

        };

    private static BackupArchiveHeader Header() =>
        new(
            BackupArchiveFormat.CurrentVersion,
            "PBKDF2-HMAC-SHA256",
            10_000,
            "AES-256-GCM",
            1024,
            CreatedAt,
            EncryptedPayloadBytes: 42);

    private static BackupManifest Manifest()
    {

        BackupManifestEntry[] entries =
        [
            Entry(
                "configuration/arcanum.json",
                size: 4,
                BackupComponent.Configuration),
        ];

        return new BackupManifest(
            BackupArchiveFormat.CurrentVersion,
            "1.0.0-test",
            "test-build",
            "20260730040000_AddBlobEncryptionMigrationState",
            CreatedAt,
            "test-platform",
            new BackupEnvelopeDescriptor(
                "PBKDF2",
                "HMAC-SHA256",
                10_000,
                Convert.ToBase64String(Salt),
                "AES-256-GCM",
                256,
                12,
                16,
                1024),
            BackupScope.ConfigurationAndAuthoredAssets,
            SessionId: null,
            RequestedIncludes:
            [
                BackupComponent.Configuration,
                BackupComponent.CompendiumSettings,
            ],
            RequestedExcludes:
            [
                BackupComponent.AuditLogs,
                BackupComponent.MasterApiKey,
            ],
            SecurityWarnings: [],
            Components: ComponentsFor(entries),
            Entries: entries);

    }

    private static BackupManifest ReplaceComponent(
        BackupManifest manifest,
        BackupComponent target,
        Func<BackupManifestComponent, BackupManifestComponent> mutate) =>
        manifest with
        {

            Components = manifest.Components
                .Select(component => component.Component == target
                    ? mutate(component)
                    : component)
                .ToArray(),

        };

    private static BackupManifestComponent[] ComponentsFor(
        IReadOnlyList<BackupManifestEntry> entries) =>
        Enum.GetValues<BackupComponent>()
            .Select(component =>
            {

                BackupManifestEntry[] owned = entries
                    .Where(entry => entry.Component == component)
                    .ToArray();

                bool complete = owned.Length > 0
                    || component == BackupComponent.CompendiumSettings;

                return new BackupManifestComponent(
                    component,
                    complete
                        ? BackupComponentStatus.Complete
                        : BackupComponentStatus.OmittedByPolicy,
                    complete ? "complete" : "omitted",
                    owned.LongLength,
                    owned.Sum(static entry => entry.Size));

            })
            .ToArray();

    private static BackupManifestEntry Entry(
        string path,
        long size,
        BackupComponent component) =>
        new(
            path,
            size,
            new string('0', 64),
            component);

}
