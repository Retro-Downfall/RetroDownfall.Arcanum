CREATE TABLE IF NOT EXISTS memory_search_documents (
    SearchId INTEGER PRIMARY KEY,
    Corpus INTEGER NOT NULL CHECK (Corpus BETWEEN 0 AND 3),
    SourceId TEXT NOT NULL,
    Owner TEXT NOT NULL,
    SortAt TEXT NOT NULL,
    SortNumber INTEGER NOT NULL,
    SortPath TEXT NOT NULL,
    ConflictOwner TEXT NOT NULL,
    ConflictGeneration TEXT NOT NULL,
    Content TEXT NOT NULL,
    AuxiliaryOne TEXT NOT NULL,
    AuxiliaryTwo TEXT NOT NULL,
    UNIQUE(Corpus, SourceId)
);
CREATE INDEX IF NOT EXISTS idx_memory_search_entry_conflict ON memory_search_documents(Corpus,Owner,SortNumber);
CREATE INDEX IF NOT EXISTS idx_memory_search_attachment_conflict ON memory_search_documents(Corpus,ConflictOwner,ConflictGeneration,SortNumber);
