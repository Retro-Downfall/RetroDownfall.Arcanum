-- ScopeKindCode, CampaignId, RetiredAtUtc, and PinnedAtUtc are laid out the way SQLite lays out an
-- added column, not the way the rest of this file is indented, and that is deliberate. Versions 2 and
-- 4 both reach an existing installation through ALTER TABLE ... ADD COLUMN, which rewrites the stored
-- table declaration by splicing ", <column-def>" in front of the closing parenthesis and taking the
-- definition verbatim. The installer then compares that stored text with this file, normalized. An
-- installation built fresh from this file and one evolved through every ALTER have to normalize to the
-- same string, so this file has to be written in the shape ALTER produces. Reindenting any of these
-- columns reports DefinitionDrift on every evolved installation and on none of the fresh ones, which is
-- the hardest shape of that failure to reproduce.
--
-- ScopeKindCode and CampaignId are separate on purpose. A single nullable CampaignId would make
-- "explicitly installation-global" and "ownership never resolved" the same null, and those two answers
-- are opposites: the first is retrievable inside every Campaign, the second inside none until an
-- operator resolves the binding. Codes 1 to 3 are the codes session_campaign_bindings.BindingKindCode
-- already uses, because a memory's scope is its owning Session's binding at the moment it was written;
-- 0 means an upgrade has not classified the row yet and is likewise retrievable nowhere.
--
-- The invariant that pair alone carries — CampaignId is present exactly when ScopeKindCode is 2 — is
-- not a table CHECK, because SQLite's ALTER cannot add one and an evolved installation could therefore
-- never match a file that declared it. SagaMemoryScopeKind and its writers own it instead.
--
-- RetiredAtUtc and PinnedAtUtc are independent of that pair and of each other, and each is nullable on
-- its own terms rather than by that invariant. A null RetiredAtUtc means the memory is active; a
-- timestamp means an operator retired it then, and saga_retirement_suppressions is what keeps the next
-- extraction pass from re-adding it. A null PinnedAtUtc means the memory carries no operator
-- protection; a timestamp means an operator pinned it then. Nothing in this step writes either column —
-- the curation verbs that will are a later step.
CREATE TABLE IF NOT EXISTS saga_memories (
    Id TEXT PRIMARY KEY,
    Content TEXT NOT NULL,
    CreatedAt TEXT NOT NULL,
    SessionId TEXT,
    Tags TEXT,
    Source TEXT
, ScopeKindCode INTEGER NOT NULL DEFAULT 0, CampaignId TEXT, RetiredAtUtc TEXT, PinnedAtUtc TEXT);

CREATE INDEX IF NOT EXISTS idx_saga_memories_session ON saga_memories(SessionId);

CREATE INDEX IF NOT EXISTS idx_saga_memories_created ON saga_memories(CreatedAt);

-- Campaign-scoped retrieval reads this on every turn, and the classification sweep pages the same
-- index looking for code 0, so the kind leads and the Campaign follows it.
CREATE INDEX IF NOT EXISTS idx_saga_memories_scope ON saga_memories(ScopeKindCode, CampaignId);
