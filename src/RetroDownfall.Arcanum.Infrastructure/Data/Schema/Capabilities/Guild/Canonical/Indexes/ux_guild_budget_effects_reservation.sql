CREATE UNIQUE INDEX IF NOT EXISTS ux_guild_budget_effects_reservation ON guild_budget_effects(ReservationId) WHERE ReservationId IS NOT NULL;
