CREATE TEMP TABLE guild_v15_operator_high_water AS SELECT COALESCE((SELECT seq FROM sqlite_sequence WHERE name='guild_operator_sequences'),0) AS Value;
