-- A content-free Core clock for bounded readers spanning several transactions.
-- Empty is revision zero. Every committed label insertion/deletion advances it;
-- rollback restores both label and clock. Optional capabilities are never named.
CREATE TABLE IF NOT EXISTS artifact_sensitivity_clock (
    Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
    Revision INTEGER NOT NULL CHECK (typeof(Revision) = 'integer' AND Revision > 0)
);
