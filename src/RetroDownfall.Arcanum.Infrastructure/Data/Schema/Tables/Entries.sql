CREATE TABLE IF NOT EXISTS "Entries" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_Entries" PRIMARY KEY,
    "SessionId" TEXT NOT NULL,
    "Role" INTEGER NOT NULL,
    "Content" TEXT NOT NULL,
    "ModelUsed" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "Sequence" INTEGER NOT NULL,
    "ToolCallId" TEXT NULL,
    "ToolName" TEXT NULL,
    "ToolArguments" TEXT NULL,
    "IsPinned" INTEGER NOT NULL DEFAULT 0,
    CONSTRAINT "FK_Entries_Sessions_SessionId" FOREIGN KEY ("SessionId") REFERENCES "Sessions" ("Id") ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS "IX_Entries_SessionId_CreatedAt" ON "Entries" ("SessionId", "CreatedAt");

-- Authoritative intra-session chronological order. Unique so a lost per-session allocation races
-- into a write failure instead of silently reordering a transcript.
CREATE UNIQUE INDEX IF NOT EXISTS "IX_Entries_SessionId_Sequence" ON "Entries" ("SessionId", "Sequence");

CREATE INDEX IF NOT EXISTS "IX_Entries_Role" ON "Entries" ("Role");

CREATE INDEX IF NOT EXISTS "IX_Entries_SessionId_IsPinned" ON "Entries" ("SessionId", "IsPinned");

-- Retention compares this column normalized - lower(replace(SessionId, '-', '')) - because a stored
-- identity once had two spellings and an exact equality would have missed one of them. SQLite cannot
-- answer a function-wrapped column from IX_Entries_SessionId_CreatedAt or from any other index above,
-- so every one of those comparisons was a full scan of this table, once per candidate Session in the
-- planning pass and again per candidate in the apply pass. The expression here has to stay character
-- for character the shape the predicate has, because that is how SQLite decides the index applies.
CREATE INDEX IF NOT EXISTS "IX_Entries_SessionId_Norm"
  ON "Entries" (lower(replace("SessionId", '-', '')));
