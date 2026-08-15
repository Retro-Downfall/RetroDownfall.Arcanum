CREATE TABLE IF NOT EXISTS capability_cleanup_state (
    -- One row per installed data-owning capability family. Campaign and Session cursors advance
    -- independently because a capability may owe cleanup for one owner kind and not the other, and
    -- coupling them would stall the whole journal behind the slower kind.
    CapabilityFamilyCode INTEGER NOT NULL PRIMARY KEY,
    AppliedCampaignSequence INTEGER NOT NULL CHECK (AppliedCampaignSequence >= 0),
    AppliedSessionSequence INTEGER NOT NULL CHECK (AppliedSessionSequence >= 0),
    FullSweepRequired INTEGER NOT NULL CHECK (FullSweepRequired IN (0, 1)),
    UpdatedAtUtc TEXT NOT NULL
);
