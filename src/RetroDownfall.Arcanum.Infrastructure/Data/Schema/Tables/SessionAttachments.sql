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
    "CreatedAt" TEXT NOT NULL,
    "SourceKind" TEXT NOT NULL DEFAULT 'SnapshotOnly',
    "SourceWorkspaceIdentity" TEXT NULL,
    "SourceRelativePath" TEXT NULL,
    "SourceCanonicalPath" TEXT NULL,
    "SourceContentSha256" TEXT NULL,
    "SourceFileIdentity" TEXT NULL,
    "SourceLastWriteAt" TEXT NULL,
    "SourceByteLength" INTEGER NULL,
    "SourceStatus" TEXT NOT NULL DEFAULT 'NotApplicable',
    "SourceDiagnosticReason" TEXT NULL,
    "EncryptionVersion" INTEGER NOT NULL DEFAULT 0,
    "EncryptionKeyId" TEXT NULL
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

CREATE INDEX IF NOT EXISTS "IX_SessionAttachments_SourceWorkspace_Path"
  ON "SessionAttachments" ("SourceWorkspaceIdentity", "SourceRelativePath");

CREATE UNIQUE INDEX IF NOT EXISTS UX_SessionAttachments_Bound
  ON SessionAttachments(SessionId, LogicalKey, Version)
  WHERE State = 'Bound';

CREATE UNIQUE INDEX IF NOT EXISTS UX_SessionAttachments_Pending
  ON SessionAttachments(PendingTurnId, LogicalKey, Version)
  WHERE State = 'Pending';

-- The two columns retention compares normalized. The attachment candidate scan reads SessionId that
-- way for every candidate Session, and the pin check reads Id that way for every candidate attachment;
-- neither can be answered from the indexes above, which key the unwrapped columns.
CREATE INDEX IF NOT EXISTS "IX_SessionAttachments_SessionId_Norm"
  ON "SessionAttachments" (lower(replace("SessionId", '-', '')));

CREATE INDEX IF NOT EXISTS "IX_SessionAttachments_Id_Norm"
  ON "SessionAttachments" (lower(replace("Id", '-', '')));
