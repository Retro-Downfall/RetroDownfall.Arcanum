CREATE VIRTUAL TABLE IF NOT EXISTS long_rest_clean_fts USING fts5(NodeId UNINDEXED, GenerationId UNINDEXED, Content, tokenize='unicode61');
