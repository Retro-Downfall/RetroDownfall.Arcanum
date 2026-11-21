CREATE TABLE IF NOT EXISTS guild_inbound_portal_messages (
    QuestId TEXT NOT NULL CHECK(QuestId=upper(QuestId) AND length(QuestId)=36 AND substr(QuestId,9,1)='-' AND substr(QuestId,14,1)='-' AND substr(QuestId,19,1)='-' AND substr(QuestId,24,1)='-' AND length(replace(QuestId,'-',''))=32 AND replace(QuestId,'-','') NOT GLOB '*[^0-9A-F]*' AND QuestId<>'00000000-0000-0000-0000-000000000000'),
    MessageId TEXT NOT NULL CHECK(MessageId=upper(MessageId) AND length(MessageId)=36 AND substr(MessageId,9,1)='-' AND substr(MessageId,14,1)='-' AND substr(MessageId,19,1)='-' AND substr(MessageId,24,1)='-' AND length(replace(MessageId,'-',''))=32 AND replace(MessageId,'-','') NOT GLOB '*[^0-9A-F]*' AND MessageId<>'00000000-0000-0000-0000-000000000000'),
    SourceStreamId TEXT NOT NULL CHECK(SourceStreamId=upper(SourceStreamId) AND length(SourceStreamId)=36 AND substr(SourceStreamId,9,1)='-' AND substr(SourceStreamId,14,1)='-' AND substr(SourceStreamId,19,1)='-' AND substr(SourceStreamId,24,1)='-' AND length(replace(SourceStreamId,'-',''))=32 AND replace(SourceStreamId,'-','') NOT GLOB '*[^0-9A-F]*' AND SourceStreamId<>'00000000-0000-0000-0000-000000000000'),
    SourceSequence INTEGER NOT NULL CHECK(typeof(SourceSequence)='integer' AND SourceSequence>=1),
    InboxId TEXT NOT NULL UNIQUE CHECK(InboxId=upper(InboxId) AND length(InboxId)=36 AND substr(InboxId,9,1)='-' AND substr(InboxId,14,1)='-' AND substr(InboxId,19,1)='-' AND substr(InboxId,24,1)='-' AND length(replace(InboxId,'-',''))=32 AND replace(InboxId,'-','') NOT GLOB '*[^0-9A-F]*' AND InboxId<>'00000000-0000-0000-0000-000000000000'),
    EnvelopeId TEXT NOT NULL CHECK(EnvelopeId=upper(EnvelopeId) AND length(EnvelopeId)=36 AND substr(EnvelopeId,9,1)='-' AND substr(EnvelopeId,14,1)='-' AND substr(EnvelopeId,19,1)='-' AND substr(EnvelopeId,24,1)='-' AND length(replace(EnvelopeId,'-',''))=32 AND replace(EnvelopeId,'-','') NOT GLOB '*[^0-9A-F]*' AND EnvelopeId<>'00000000-0000-0000-0000-000000000000'),
    PRIMARY KEY(QuestId,MessageId),
    UNIQUE(QuestId,SourceStreamId,SourceSequence),
    FOREIGN KEY(InboxId,QuestId,EnvelopeId) REFERENCES guild_inbox(Id,RecipientQuestId,EnvelopeId) ON DELETE RESTRICT
);
