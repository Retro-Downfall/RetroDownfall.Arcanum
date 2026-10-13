CREATE TRIGGER IF NOT EXISTS long_rest_work_item_delete
BEFORE DELETE ON long_rest_work_items
BEGIN
    DELETE FROM long_rest_provider_effects WHERE WorkItemId = OLD.WorkItemId;
    DELETE FROM long_rest_work_inputs WHERE WorkItemId = OLD.WorkItemId;
    DELETE FROM long_rest_accepted_summaries WHERE WorkItemId = OLD.WorkItemId;
    DELETE FROM long_rest_adjudications WHERE WorkItemId = OLD.WorkItemId;
END;
