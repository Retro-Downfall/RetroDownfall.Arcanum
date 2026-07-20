CREATE TABLE IF NOT EXISTS "SessionAttachments" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_SessionAttachments" PRIMARY KEY,
    "SessionId" TEXT NULL,
    "EntryId" TEXT NULL,
    "PendingTurnId" TEXT NULL,
    "State" TEXT NOT NULL,
    "LogicalKey" TEXT NOT NULL,
    "OriginalFileName" TEXT NOT NULL,
    "Version" INTEGER NOT NULL,
    "RelativePath" TEXT NOT NULL,
    "ContentSha256" TEXT NOT NULL,
    "MimeType" TEXT NOT NULL,
    "ByteLength" INTEGER NOT NULL,
    "Kind" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS "IX_SessionAttachments_Session_Logical_Version"
  ON "SessionAttachments" ("SessionId", "LogicalKey", "Version");

CREATE INDEX IF NOT EXISTS "IX_SessionAttachments_Session_CreatedAt"
  ON "SessionAttachments" ("SessionId", "CreatedAt");

CREATE INDEX IF NOT EXISTS "IX_SessionAttachments_EntryId"
  ON "SessionAttachments" ("EntryId");

CREATE INDEX IF NOT EXISTS "IX_SessionAttachments_PendingTurnId"
  ON "SessionAttachments" ("PendingTurnId");

CREATE INDEX IF NOT EXISTS "IX_SessionAttachments_State"
  ON "SessionAttachments" ("State");
