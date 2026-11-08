CREATE TABLE IF NOT EXISTS guild_budget_effects (
    EffectId TEXT NOT NULL PRIMARY KEY CHECK(EffectId=upper(EffectId) AND length(EffectId)=36 AND substr(EffectId,9,1)='-' AND substr(EffectId,14,1)='-' AND substr(EffectId,19,1)='-' AND substr(EffectId,24,1)='-' AND length(replace(EffectId,'-',''))=32 AND replace(EffectId,'-','') NOT GLOB '*[^0-9A-F]*' AND EffectId<>'00000000-0000-0000-0000-000000000000'),
    AccountId TEXT NOT NULL REFERENCES guild_budget_accounts(AccountId) ON DELETE RESTRICT CHECK(AccountId=upper(AccountId) AND length(AccountId)=36 AND substr(AccountId,9,1)='-' AND substr(AccountId,14,1)='-' AND substr(AccountId,19,1)='-' AND substr(AccountId,24,1)='-' AND length(replace(AccountId,'-',''))=32 AND replace(AccountId,'-','') NOT GLOB '*[^0-9A-F]*' AND AccountId<>'00000000-0000-0000-0000-000000000000'),
    QuestId TEXT NOT NULL REFERENCES guild_quests(Id) ON DELETE RESTRICT CHECK(QuestId=upper(QuestId) AND length(QuestId)=36 AND substr(QuestId,9,1)='-' AND substr(QuestId,14,1)='-' AND substr(QuestId,19,1)='-' AND substr(QuestId,24,1)='-' AND length(replace(QuestId,'-',''))=32 AND replace(QuestId,'-','') NOT GLOB '*[^0-9A-F]*' AND QuestId<>'00000000-0000-0000-0000-000000000000'),
    DispatchId TEXT NOT NULL REFERENCES guild_dispatches(DispatchId) ON DELETE RESTRICT CHECK(DispatchId=upper(DispatchId) AND length(DispatchId)=36 AND substr(DispatchId,9,1)='-' AND substr(DispatchId,14,1)='-' AND substr(DispatchId,19,1)='-' AND substr(DispatchId,24,1)='-' AND length(replace(DispatchId,'-',''))=32 AND replace(DispatchId,'-','') NOT GLOB '*[^0-9A-F]*' AND DispatchId<>'00000000-0000-0000-0000-000000000000'),
    AccountingAssociationId TEXT NOT NULL CHECK(AccountingAssociationId=upper(AccountingAssociationId) AND length(AccountingAssociationId)=36 AND substr(AccountingAssociationId,9,1)='-' AND substr(AccountingAssociationId,14,1)='-' AND substr(AccountingAssociationId,19,1)='-' AND substr(AccountingAssociationId,24,1)='-' AND length(replace(AccountingAssociationId,'-',''))=32 AND replace(AccountingAssociationId,'-','') NOT GLOB '*[^0-9A-F]*' AND AccountingAssociationId<>'00000000-0000-0000-0000-000000000000'),
    KindCode INTEGER NOT NULL CHECK(typeof(KindCode)='integer' AND KindCode IN(0,1)),
    SourceId TEXT NOT NULL CHECK(SourceId=upper(SourceId) AND length(SourceId)=36 AND substr(SourceId,9,1)='-' AND substr(SourceId,14,1)='-' AND substr(SourceId,19,1)='-' AND substr(SourceId,24,1)='-' AND length(replace(SourceId,'-',''))=32 AND replace(SourceId,'-','') NOT GLOB '*[^0-9A-F]*' AND SourceId<>'00000000-0000-0000-0000-000000000000'),
    ReservationId TEXT NULL CHECK(ReservationId IS NULL OR (ReservationId=upper(ReservationId) AND length(ReservationId)=36 AND substr(ReservationId,9,1)='-' AND substr(ReservationId,14,1)='-' AND substr(ReservationId,19,1)='-' AND substr(ReservationId,24,1)='-' AND length(replace(ReservationId,'-',''))=32 AND replace(ReservationId,'-','') NOT GLOB '*[^0-9A-F]*' AND ReservationId<>'00000000-0000-0000-0000-000000000000')),
    UsdBound TEXT NULL CHECK(UsdBound IS NULL OR typeof(UsdBound)='text'),
    TokenBound INTEGER NULL CHECK(TokenBound IS NULL OR (typeof(TokenBound)='integer' AND TokenBound>=0)),
    KnownUsd TEXT NOT NULL DEFAULT '0' CHECK(typeof(KnownUsd)='text'),
    KnownTokens INTEGER NOT NULL DEFAULT 0 CHECK(typeof(KnownTokens)='integer' AND KnownTokens>=0),
    UnknownUsd INTEGER NOT NULL DEFAULT 0 CHECK(typeof(UnknownUsd)='integer' AND UnknownUsd IN(0,1)),
    UnknownTokens INTEGER NOT NULL DEFAULT 0 CHECK(typeof(UnknownTokens)='integer' AND UnknownTokens IN(0,1)),
    StateCode INTEGER NOT NULL CHECK(typeof(StateCode)='integer' AND StateCode IN(0,1,2,3)),
    Revision INTEGER NOT NULL CHECK(typeof(Revision)='integer' AND Revision>=1),
    UNIQUE(KindCode,SourceId)
);
