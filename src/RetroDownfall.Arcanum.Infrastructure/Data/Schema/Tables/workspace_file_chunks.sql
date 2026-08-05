CREATE TABLE IF NOT EXISTS workspace_file_chunks (
    ChunkId TEXT PRIMARY KEY,
    WorkspacePath TEXT NOT NULL,
    RelativePath TEXT NOT NULL,
    ChunkIndex INTEGER NOT NULL,
    Content TEXT NOT NULL,
    CharOffset INTEGER NOT NULL,
    CharLength INTEGER NOT NULL,
    StartLine INTEGER NOT NULL DEFAULT 1,
    EndLine INTEGER NOT NULL DEFAULT 1,
    FileLastWriteTime TEXT NOT NULL,
    IndexedAt TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_workspace_file_chunks_path
ON workspace_file_chunks(WorkspacePath, RelativePath);
