CREATE TABLE IF NOT EXISTS "UploadedFiles" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_UploadedFiles" PRIMARY KEY,
    "Filename" TEXT NOT NULL,
    "Bytes" INTEGER NOT NULL,
    "Purpose" TEXT NOT NULL,
    "MimeType" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "EncryptionVersion" INTEGER NOT NULL DEFAULT 0,
    "EncryptionKeyId" TEXT NULL,
    "PlaintextSha256" TEXT NULL
);

CREATE INDEX IF NOT EXISTS "IX_UploadedFiles_Purpose" ON "UploadedFiles" ("Purpose");

CREATE INDEX IF NOT EXISTS "IX_UploadedFiles_CreatedAt" ON "UploadedFiles" ("CreatedAt");
