ALTER TABLE annal_versions ADD COLUMN ContentHashFormatCode INTEGER NOT NULL DEFAULT 1 CHECK (ContentHashFormatCode IN (1, 2));
