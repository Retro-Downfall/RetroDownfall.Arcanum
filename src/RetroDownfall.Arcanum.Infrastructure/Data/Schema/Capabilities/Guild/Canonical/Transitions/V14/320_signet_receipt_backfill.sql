UPDATE guild_signet_revocations AS r SET AcceptedAtUtc=(
    SELECT e.CreatedAtUtc FROM guild_events e
    WHERE e.ObjectId=r.SignetId AND e.Revision=r.Revision AND e.EventKindCode=3 AND e.SourceKindCode=6
    LIMIT 1)
WHERE (SELECT COUNT(*) FROM guild_signet_revocations other WHERE other.SignetId=r.SignetId AND other.Revision=r.Revision)=1
    AND (SELECT COUNT(*) FROM guild_events e WHERE e.ObjectId=r.SignetId AND e.Revision=r.Revision AND e.EventKindCode=3 AND e.SourceKindCode=6)=1;
