CREATE TABLE IF NOT EXISTS campaign_path_identities (
    -- One resolved workspace root per Campaign, keyed by the Campaign itself. The cascade keeps core
    -- Campaign deletion from ever being blocked by workspace state: the marker cleanup intent is the
    -- durable record of external work still owed, and it deliberately survives this row.
    CampaignId TEXT NOT NULL PRIMARY KEY REFERENCES "Campaigns"("Id") ON DELETE CASCADE,
    -- The policy version that produced the identity below. Derivation inputs change over time, so a
    -- digest is only comparable against one that was derived the same way.
    PolicyVersion INTEGER NOT NULL CHECK (PolicyVersion > 0),
    -- Every registration, repair, and path update advances this. Dispatch and workspace-tool
    -- boundaries capture it and compare later, so a remap cannot redirect an in-flight tool.
    Revision INTEGER NOT NULL CHECK (Revision > 0),
    DisplayPath TEXT NOT NULL CHECK (length(DisplayPath) BETWEEN 1 AND 4096),
    Depth INTEGER NOT NULL CHECK (Depth BETWEEN 1 AND 256),
    -- The opaque keyed identity of the physical directory, never the path text. It is unique because
    -- two Campaigns resolving to one directory would let either one act with the other's authority;
    -- a copied marker on a different volume or inode therefore fails to register rather than
    -- silently sharing a root.
    PhysicalIdentityDigest BLOB NOT NULL CHECK (length(PhysicalIdentityDigest) = 32),
    UpdatedAtUtc TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_campaign_path_identities_physical_identity
    ON campaign_path_identities(PhysicalIdentityDigest);
