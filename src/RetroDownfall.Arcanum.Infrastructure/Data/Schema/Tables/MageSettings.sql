CREATE TABLE IF NOT EXISTS "MageSettings" (
    "Key" TEXT NOT NULL CONSTRAINT "PK_MageSettings" PRIMARY KEY,
    "Value" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL
);
