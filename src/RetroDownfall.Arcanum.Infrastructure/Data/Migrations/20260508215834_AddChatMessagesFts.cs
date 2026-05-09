using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RetroDownfall.Arcanum.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChatMessagesFts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                CREATE VIRTUAL TABLE IF NOT EXISTS ChatMessages_fts USING fts5(
                    Id UNINDEXED,
                    ConversationId UNINDEXED,
                    Role UNINDEXED,
                    Content
                );

                CREATE TRIGGER IF NOT EXISTS ChatMessages_ai AFTER INSERT ON ChatMessages BEGIN
                    INSERT INTO ChatMessages_fts(Id, ConversationId, Role, Content)
                    VALUES (new.Id, new.ConversationId, new.Role, new.Content);
                END;

                CREATE TRIGGER IF NOT EXISTS ChatMessages_ad AFTER DELETE ON ChatMessages BEGIN
                    DELETE FROM ChatMessages_fts WHERE Id = old.Id;
                END;

                CREATE TRIGGER IF NOT EXISTS ChatMessages_au AFTER UPDATE ON ChatMessages BEGIN
                    DELETE FROM ChatMessages_fts WHERE Id = old.Id;
                    INSERT INTO ChatMessages_fts(Id, ConversationId, Role, Content)
                    VALUES (new.Id, new.ConversationId, new.Role, new.Content);
                END;

                INSERT INTO ChatMessages_fts(Id, ConversationId, Role, Content)
                SELECT Id, ConversationId, Role, Content FROM ChatMessages;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP TRIGGER IF EXISTS ChatMessages_ai;
                DROP TRIGGER IF EXISTS ChatMessages_ad;
                DROP TRIGGER IF EXISTS ChatMessages_au;
                DROP TABLE IF EXISTS ChatMessages_fts;
            ");
        }
    }
}
