ALTER TABLE "UploadedFiles" ADD COLUMN "EncryptionVersion" INTEGER NOT NULL DEFAULT 0;
ALTER TABLE "UploadedFiles" ADD COLUMN "EncryptionKeyId" TEXT NULL;

ALTER TABLE "SessionAttachments" ADD COLUMN "EncryptionVersion" INTEGER NOT NULL DEFAULT 0;
ALTER TABLE "SessionAttachments" ADD COLUMN "EncryptionKeyId" TEXT NULL;
