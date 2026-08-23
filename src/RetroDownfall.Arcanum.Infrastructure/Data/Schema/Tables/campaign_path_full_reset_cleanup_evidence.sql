-- The per-Campaign evidence a full installation reset authenticated *before* it touched either
-- host-tools marker, kept beside its kind-four intent rather than inside it. The parent row owns
-- identity and phase for every intent kind; only kind four has an inventory entry behind it, and
-- widening the parent with six columns no other kind may set would make the kind-four shape a
-- property of every row instead of a property of this one.
--
-- Nothing here is filesystem authority. There is no path, no marker payload, no handle, and no
-- capability — only digests of what was observed, so a replay can prove it is looking at the same
-- Campaign in the same place without being able to reconstruct either.
CREATE TABLE IF NOT EXISTS campaign_path_full_reset_cleanup_evidence (
    -- One-to-one with the intent, and cascaded rather than independently retained: this row is only
    -- meaningful as the evidence behind that exact child.
    IntentId TEXT NOT NULL PRIMARY KEY
        REFERENCES campaign_path_marker_intents(IntentId) ON DELETE CASCADE,
    -- The authenticated inventory entry this child answers, as it was hashed into the inventory
    -- vector before PairJournaled. A replay that cannot reproduce it is looking at a different
    -- inventory, whatever its count says.
    CampaignInventoryEntryDigest BLOB NOT NULL CHECK (length(CampaignInventoryEntryDigest) = 32),
    IndexedPhysicalIdentityDigest BLOB NOT NULL CHECK (length(IndexedPhysicalIdentityDigest) = 32),
    CanonicalDisplayPathDigest BLOB NOT NULL CHECK (length(CanonicalDisplayPathDigest) = 32),
    -- What the authenticated pre-effect inventory expected the same-handle ownership evidence to be.
    SameHandleOwnershipEvidenceDigest BLOB NOT NULL CHECK (length(SameHandleOwnershipEvidenceDigest) = 32),
    -- Opened = 1, Unavailable = 2, Mismatch = 3. Closed on purpose: an unrecognized observation is
    -- not a fourth arm to be interpreted later, it is a row that must never have been written.
    ObservationCode INTEGER NOT NULL CHECK (ObservationCode IN (1, 2, 3)),
    -- What was actually observed from the retained handle, present only for an opened root. The two
    -- blocked arms never opened one, so a digest here would be evidence taken from nothing.
    OpenedSameHandleOwnershipEvidenceDigest BLOB NULL
        CHECK (OpenedSameHandleOwnershipEvidenceDigest IS NULL
            OR length(OpenedSameHandleOwnershipEvidenceDigest) = 32),
    ObservationDigest BLOB NOT NULL CHECK (length(ObservationDigest) = 32),
    -- An opened root is adopted only when what was observed equals what was authenticated, and the
    -- equality is enforced here rather than only in the caller: a row that stored a different opened
    -- digest would already be a usable deletion authority by the time anything compared them.
    CHECK (
        (ObservationCode = 1
            AND OpenedSameHandleOwnershipEvidenceDigest IS NOT NULL
            AND OpenedSameHandleOwnershipEvidenceDigest = SameHandleOwnershipEvidenceDigest)
        OR (ObservationCode IN (2, 3) AND OpenedSameHandleOwnershipEvidenceDigest IS NULL)
    )
);
