CREATE TABLE IF NOT EXISTS session_campaign_bindings (
    -- Exactly one row per Session. A missing row is an integrity failure rather than an implicit
    -- Global Session, which is why the binding lives here instead of being inferred from the
    -- nullable legacy navigation column on "Sessions". The cascade is the point of the reference:
    -- Session retention removes the binding with its Session rather than leaving an orphan that a
    -- later Session ID collision could read as authority.
    SessionId TEXT NOT NULL PRIMARY KEY REFERENCES "Sessions"("Id") ON DELETE CASCADE,
    BindingKindCode INTEGER NOT NULL CHECK (BindingKindCode IN (1, 2, 3)),
    -- Deliberately unconstrained by a foreign key: this is the historical authority identity, and
    -- Campaign deletion must be able to clear its own row without deleting or rewriting the durable
    -- fact that this Session was bound to that Campaign.
    CampaignId TEXT NULL,
    BoundAtUtc TEXT NOT NULL,
    -- GlobalOnly and LegacyUnresolved carry no Campaign; Campaign carries exactly one. An unresolved
    -- legacy row supplies no authority at all, so it must not be able to hold a Campaign identity a
    -- reader could mistake for a resolved binding.
    CHECK (
        (BindingKindCode IN (1, 3) AND CampaignId IS NULL)
        OR (BindingKindCode = 2 AND CampaignId IS NOT NULL)
    )
);

-- Campaign deletion and Campaign-scoped inventory both need every Session bound to one Campaign
-- without scanning the table.
CREATE INDEX IF NOT EXISTS idx_session_campaign_bindings_campaign
    ON session_campaign_bindings(CampaignId);

-- The resolution inventory pages over unresolved rows only. They are a vanishing fraction of the
-- table once an upgraded installation finishes resolving, so a partial index keeps that scan
-- proportional to the work remaining rather than to the Session count.
CREATE INDEX IF NOT EXISTS idx_session_campaign_bindings_unresolved
    ON session_campaign_bindings(SessionId) WHERE BindingKindCode = 3;
