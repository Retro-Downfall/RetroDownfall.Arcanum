CREATE VIRTUAL TABLE IF NOT EXISTS memory_search_fts USING fts5(
    Content,
    AuxiliaryOne,
    AuxiliaryTwo,
    content='memory_search_documents',
    content_rowid='SearchId',
    tokenize='trigram case_sensitive 1',
    detail=none
);
