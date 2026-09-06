-- A nullable value keeps inherited finalization guards conservative: their exact original frontier
-- cannot be reconstructed after later turns have appended entries. New native finalizations always
-- store the sequence captured by their transaction.
ALTER TABLE assistant_entry_finalizations ADD COLUMN ThroughEntrySequence INTEGER NULL CHECK (ThroughEntrySequence IS NULL OR ThroughEntrySequence >= 0);
