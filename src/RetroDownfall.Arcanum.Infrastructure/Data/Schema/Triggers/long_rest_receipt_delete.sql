CREATE TRIGGER IF NOT EXISTS long_rest_receipt_delete
BEFORE DELETE ON long_rest_receipts
BEGIN
    DELETE FROM long_rest_protected_receipt_inputs WHERE ReceiptId = OLD.ReceiptId;
    DELETE FROM long_rest_protected_receipt_evidence WHERE ReceiptId = OLD.ReceiptId;
    DELETE FROM long_rest_work_items WHERE ReceiptId = OLD.ReceiptId;
END;
