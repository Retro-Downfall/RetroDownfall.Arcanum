CREATE VIRTUAL TABLE IF NOT EXISTS long_rest_protected_fts USING fts5(NodeId UNINDEXED, GenerationId, Content, tokenize='unicode61');
