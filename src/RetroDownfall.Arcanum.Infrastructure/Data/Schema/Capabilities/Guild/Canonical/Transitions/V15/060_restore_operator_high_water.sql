UPDATE sqlite_sequence SET seq=max(seq,(SELECT Value FROM guild_v15_operator_high_water)) WHERE name='guild_operator_sequences';
