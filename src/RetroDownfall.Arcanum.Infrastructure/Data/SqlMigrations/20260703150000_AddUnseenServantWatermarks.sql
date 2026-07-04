CREATE TABLE "UnseenServantWatermarks" (
    "JobKey" TEXT NOT NULL CONSTRAINT "PK_UnseenServantWatermarks" PRIMARY KEY,
    "LastRunAt" TEXT NOT NULL,
    "EffectiveIntervalMinutes" INTEGER NOT NULL DEFAULT 0
);
