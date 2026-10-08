CREATE TABLE IF NOT EXISTS "SessionContextPins" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_SessionContextPins" PRIMARY KEY,
    "SessionId" TEXT NOT NULL,
    "Kind" INTEGER NOT NULL,
    "TargetIdentifier" TEXT NOT NULL,
    "DisplayLabel" TEXT NOT NULL,
    "ContentVersion" TEXT NULL,
    "CreatedAt" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL,
    CONSTRAINT "FK_SessionContextPins_Sessions_SessionId"
        FOREIGN KEY ("SessionId") REFERENCES "Sessions" ("Id") ON DELETE CASCADE
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_SessionContextPins_SessionId_Kind_TargetIdentifier"
    ON "SessionContextPins" ("SessionId", "Kind", "TargetIdentifier");

CREATE INDEX IF NOT EXISTS "IX_SessionContextPins_SessionId_UpdatedAt"
    ON "SessionContextPins" ("SessionId", "UpdatedAt");

-- Retention compares SessionId normalized - lower(replace(SessionId, '-', '')) - when a Session is
-- deleted, both in the delete itself and in the post-commit count that proves it, and SQLite cannot
-- answer a function-wrapped column from an ordinary column index. The expression here has to stay
-- character for character the shape the predicate has, because that is how SQLite decides the index
-- applies.
CREATE INDEX IF NOT EXISTS "IX_SessionContextPins_SessionId_Norm"
    ON "SessionContextPins" (lower(replace("SessionId", '-', '')));
