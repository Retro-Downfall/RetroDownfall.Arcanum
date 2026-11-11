ALTER TABLE guild_cohorts ADD COLUMN OfferSequence INTEGER NOT NULL DEFAULT 0 CHECK(OfferSequence>=0);
