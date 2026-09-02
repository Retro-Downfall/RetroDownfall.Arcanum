-- FileLength is laid out the way SQLite lays out an added column, not the way the rest of this file is
-- indented, and that is deliberate. Version 6 reaches an existing installation through
-- ALTER TABLE ... ADD COLUMN, which rewrites the stored table declaration by splicing ", <column-def>"
-- in front of the closing parenthesis and taking the definition verbatim. The installer then compares
-- that stored text with this file, normalized. An installation built fresh from this file and one
-- evolved through the ALTER have to normalize to the same string, so this file has to be written in the
-- shape ALTER produces. Reindenting it reports DefinitionDrift on every evolved installation and on none
-- of the fresh ones, which is the hardest shape of that failure to reproduce.
--
-- It is here because FileLastWriteTime alone cannot answer "has this file changed". The sweep compares
-- the recorded timestamp for equality, so a rewrite that lands on the recorded value - a coarse-grained
-- filesystem stamping within the same tick, an archive extraction restoring what it captured, a tool
-- that puts the timestamp back on purpose - makes the file invisible to every later tick, permanently,
-- because nothing else revisits it. The length is the second signal, and it is a signal rather than a
-- proof: a rewrite preserving both the timestamp and the byte count is still missed. Hashing every
-- candidate on every tick would be a proof, and would read the whole workspace to find the nothing that
-- usually changed.
--
-- -1 means "no length was recorded", which is what every row an upgrade inherits holds. It is not a
-- length any file can have, so the comparison treats such a row as changed and re-indexes it once,
-- after which it holds a real value. The same sentinel is written at insert and replaced by the
-- metadata pass that stamps the timestamp, so a file whose indexing was interrupted is re-indexed for
-- the same reason rather than being trusted half-written.
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
, FileLength INTEGER NOT NULL DEFAULT -1);

CREATE INDEX IF NOT EXISTS idx_workspace_file_chunks_path
ON workspace_file_chunks(WorkspacePath, RelativePath);
