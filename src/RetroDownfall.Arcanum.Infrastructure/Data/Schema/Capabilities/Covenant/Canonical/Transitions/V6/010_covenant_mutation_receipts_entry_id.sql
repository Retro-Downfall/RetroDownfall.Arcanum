-- SQLite splices the added column in front of the comma that opens the table-constraint list, which is
-- exactly where the head file spells it, so the evolved declaration matches a fresh one. Receipts
-- written before this step keep a null EntryId.
ALTER TABLE covenant_mutation_receipts ADD COLUMN EntryId TEXT NULL;
