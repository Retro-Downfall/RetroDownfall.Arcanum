INSERT INTO guild_operator_sequences(Sequence,KindCode,ObjectId) SELECT IssuedSequence,3,Id FROM guild_quests ORDER BY IssuedSequence,Id;
