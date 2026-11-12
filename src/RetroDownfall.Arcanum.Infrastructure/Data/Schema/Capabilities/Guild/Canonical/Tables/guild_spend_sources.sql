CREATE TABLE IF NOT EXISTS guild_spend_sources (
    EffectId TEXT NOT NULL PRIMARY KEY REFERENCES guild_budget_effects(EffectId) ON DELETE RESTRICT CHECK(typeof(EffectId)='text' AND EffectId=upper(EffectId) AND length(EffectId)=36 AND substr(EffectId,9,1)='-' AND substr(EffectId,14,1)='-' AND substr(EffectId,19,1)='-' AND substr(EffectId,24,1)='-' AND length(replace(EffectId,'-',''))=32 AND replace(EffectId,'-','') NOT GLOB '*[^0-9A-F]*' AND EffectId<>'00000000-0000-0000-0000-000000000000'),
    SourceId TEXT NOT NULL UNIQUE CHECK(typeof(SourceId)='text' AND SourceId=upper(SourceId) AND length(SourceId)=36 AND substr(SourceId,9,1)='-' AND substr(SourceId,14,1)='-' AND substr(SourceId,19,1)='-' AND substr(SourceId,24,1)='-' AND length(replace(SourceId,'-',''))=32 AND replace(SourceId,'-','') NOT GLOB '*[^0-9A-F]*' AND SourceId<>'00000000-0000-0000-0000-000000000000'),
    SourceKindCode INTEGER NOT NULL CHECK(typeof(SourceKindCode)='integer' AND SourceKindCode IN(0,1)),
    SourceVersion INTEGER NULL CHECK(SourceVersion IS NULL OR (typeof(SourceVersion)='integer' AND SourceVersion>=1)),
    InstallationGeneration TEXT NULL CHECK(InstallationGeneration IS NULL OR (typeof(InstallationGeneration)='text' AND InstallationGeneration=upper(InstallationGeneration) AND length(InstallationGeneration)=36 AND substr(InstallationGeneration,9,1)='-' AND substr(InstallationGeneration,14,1)='-' AND substr(InstallationGeneration,19,1)='-' AND substr(InstallationGeneration,24,1)='-' AND length(replace(InstallationGeneration,'-',''))=32 AND replace(InstallationGeneration,'-','') NOT GLOB '*[^0-9A-F]*' AND InstallationGeneration<>'00000000-0000-0000-0000-000000000000')),
    AuthorityEpoch TEXT NULL CHECK(AuthorityEpoch IS NULL OR (typeof(AuthorityEpoch)='text' AND AuthorityEpoch=upper(AuthorityEpoch) AND length(AuthorityEpoch)=36 AND substr(AuthorityEpoch,9,1)='-' AND substr(AuthorityEpoch,14,1)='-' AND substr(AuthorityEpoch,19,1)='-' AND substr(AuthorityEpoch,24,1)='-' AND length(replace(AuthorityEpoch,'-',''))=32 AND replace(AuthorityEpoch,'-','') NOT GLOB '*[^0-9A-F]*' AND AuthorityEpoch<>'00000000-0000-0000-0000-000000000000')),
    AuthorityKeyId TEXT NULL CHECK(AuthorityKeyId IS NULL OR (typeof(AuthorityKeyId)='text' AND AuthorityKeyId=upper(AuthorityKeyId) AND length(AuthorityKeyId)=36 AND substr(AuthorityKeyId,9,1)='-' AND substr(AuthorityKeyId,14,1)='-' AND substr(AuthorityKeyId,19,1)='-' AND substr(AuthorityKeyId,24,1)='-' AND length(replace(AuthorityKeyId,'-',''))=32 AND replace(AuthorityKeyId,'-','') NOT GLOB '*[^0-9A-F]*' AND AuthorityKeyId<>'00000000-0000-0000-0000-000000000000')),
    Endpoint TEXT NULL CHECK(Endpoint IS NULL OR typeof(Endpoint)='text'),
    Provider TEXT NULL CHECK(Provider IS NULL OR typeof(Provider)='text'),
    Model TEXT NULL CHECK(Model IS NULL OR typeof(Model)='text'),
    PricingSnapshotJson TEXT NULL CHECK(PricingSnapshotJson IS NULL OR typeof(PricingSnapshotJson)='text'),
    CredentialFingerprint BLOB NULL CHECK(CredentialFingerprint IS NULL OR (typeof(CredentialFingerprint)='blob' AND length(CredentialFingerprint)=32)),
    GenerationId TEXT NULL CHECK(GenerationId IS NULL OR (typeof(GenerationId)='text' AND length(GenerationId)>0)),
    InitialSnapshot BLOB NULL CHECK(InitialSnapshot IS NULL OR typeof(InitialSnapshot)='blob'),
    InitialDigest BLOB NULL CHECK(InitialDigest IS NULL OR (typeof(InitialDigest)='blob' AND length(InitialDigest)=32)),
    InitialOutcome BLOB NULL CHECK(InitialOutcome IS NULL OR typeof(InitialOutcome)='blob'),
    InitialEventSequence INTEGER NULL CHECK(InitialEventSequence IS NULL OR (typeof(InitialEventSequence)='integer' AND InitialEventSequence>=1)),
    CHECK((SourceKindCode=0 AND SourceVersion IS NULL AND InstallationGeneration IS NULL AND AuthorityEpoch IS NULL AND AuthorityKeyId IS NULL AND Endpoint IS NULL AND Provider IS NULL AND Model IS NULL AND PricingSnapshotJson IS NULL AND CredentialFingerprint IS NULL AND GenerationId IS NULL)
       OR (SourceKindCode=1 AND SourceVersion IS NOT NULL AND InstallationGeneration IS NOT NULL AND AuthorityEpoch IS NOT NULL AND AuthorityKeyId IS NOT NULL AND Endpoint IS NOT NULL AND Provider IS NOT NULL AND Model IS NOT NULL AND PricingSnapshotJson IS NOT NULL AND CredentialFingerprint IS NOT NULL)),
    CHECK((InitialSnapshot IS NULL AND InitialDigest IS NULL AND InitialOutcome IS NULL AND InitialEventSequence IS NULL)
       OR (InitialSnapshot IS NOT NULL AND InitialDigest IS NOT NULL AND InitialOutcome IS NOT NULL AND InitialEventSequence IS NOT NULL))
);
