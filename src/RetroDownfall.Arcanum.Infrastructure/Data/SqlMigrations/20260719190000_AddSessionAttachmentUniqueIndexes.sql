CREATE UNIQUE INDEX IF NOT EXISTS UX_SessionAttachments_Bound
  ON SessionAttachments(SessionId, LogicalKey, Version)
  WHERE State = 'Bound';

CREATE UNIQUE INDEX IF NOT EXISTS UX_SessionAttachments_Pending
  ON SessionAttachments(PendingTurnId, LogicalKey, Version)
  WHERE State = 'Pending';
