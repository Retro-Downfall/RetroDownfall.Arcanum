CREATE TRIGGER IF NOT EXISTS long_rest_operation_delete
BEFORE DELETE ON LongRunningOperations
BEGIN
    DELETE FROM long_rest_discovery_generations WHERE OperationId = OLD.Id;
    DELETE FROM long_rest_sweeps WHERE OperationId = OLD.Id;
END;
