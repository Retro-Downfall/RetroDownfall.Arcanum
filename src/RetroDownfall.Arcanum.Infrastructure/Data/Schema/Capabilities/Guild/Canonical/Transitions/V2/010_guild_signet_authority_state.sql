CREATE TABLE IF NOT EXISTS guild_signet_authority_state (
    StateKey INTEGER NOT NULL PRIMARY KEY CHECK(StateKey=1),
    Epoch TEXT NOT NULL CHECK(length(Epoch)=36)
);
