using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RetroDownfall.Arcanum.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RenameSessionAndEntry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Apprentices_Conversations_ConversationId",
                table: "Apprentices");

            migrationBuilder.DropForeignKey(
                name: "FK_ChatMessages_Conversations_ConversationId",
                table: "ChatMessages");

            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS ChatMessages_ai;
                DROP TRIGGER IF EXISTS ChatMessages_ad;
                DROP TRIGGER IF EXISTS ChatMessages_au;
                DROP TABLE IF EXISTS ChatMessages_fts;
                """);

            migrationBuilder.RenameTable(
                name: "Conversations",
                newName: "Sessions");

            migrationBuilder.RenameTable(
                name: "ChatMessages",
                newName: "Entries");

            migrationBuilder.RenameIndex(
                name: "IX_Conversations_CreatedAt",
                table: "Sessions",
                newName: "IX_Sessions_CreatedAt");

            migrationBuilder.RenameColumn(
                name: "ConversationId",
                table: "Entries",
                newName: "SessionId");

            migrationBuilder.RenameColumn(
                name: "Timestamp",
                table: "Entries",
                newName: "CreatedAt");

            migrationBuilder.RenameIndex(
                name: "IX_ChatMessages_ConversationId_Timestamp",
                table: "Entries",
                newName: "IX_Entries_SessionId_CreatedAt");

            migrationBuilder.RenameColumn(
                name: "ConversationId",
                table: "Apprentices",
                newName: "SessionId");

            migrationBuilder.RenameIndex(
                name: "IX_Apprentices_ConversationId",
                table: "Apprentices",
                newName: "IX_Apprentices_SessionId");

            migrationBuilder.AddColumn<Guid>(
                name: "CampaignId",
                table: "Sessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Sessions",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "active");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "Sessions",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), TimeSpan.Zero));

            migrationBuilder.Sql(
                """
                UPDATE "Sessions" SET "UpdatedAt" = "CreatedAt" WHERE "UpdatedAt" LIKE '0001-01-01%';
                """);

            migrationBuilder.AlterColumn<string>(
                name: "Title",
                table: "Sessions",
                type: "TEXT",
                maxLength: 512,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 512);

            migrationBuilder.AddColumn<string>(
                name: "ToolArguments",
                table: "Entries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ToolCallId",
                table: "Entries",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ToolName",
                table: "Entries",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_Status",
                table: "Sessions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_UpdatedAt",
                table: "Sessions",
                column: "UpdatedAt");

            migrationBuilder.AddForeignKey(
                name: "FK_Entries_Sessions_SessionId",
                table: "Entries",
                column: "SessionId",
                principalTable: "Sessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Apprentices_Sessions_SessionId",
                table: "Apprentices",
                column: "SessionId",
                principalTable: "Sessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.Sql(
                """
                CREATE VIRTUAL TABLE IF NOT EXISTS Entries_fts USING fts5(
                    Id UNINDEXED,
                    SessionId UNINDEXED,
                    Role UNINDEXED,
                    Content
                );

                CREATE TRIGGER IF NOT EXISTS Entries_ai AFTER INSERT ON Entries BEGIN
                    INSERT INTO Entries_fts(Id, SessionId, Role, Content)
                    VALUES (new.Id, new.SessionId, new.Role, new.Content);
                END;

                CREATE TRIGGER IF NOT EXISTS Entries_ad AFTER DELETE ON Entries BEGIN
                    DELETE FROM Entries_fts WHERE Id = old.Id;
                END;

                CREATE TRIGGER IF NOT EXISTS Entries_au AFTER UPDATE ON Entries BEGIN
                    DELETE FROM Entries_fts WHERE Id = old.Id;
                    INSERT INTO Entries_fts(Id, SessionId, Role, Content)
                    VALUES (new.Id, new.SessionId, new.Role, new.Content);
                END;

                INSERT INTO Entries_fts(Id, SessionId, Role, Content)
                SELECT Id, SessionId, Role, Content FROM Entries;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Apprentices_Sessions_SessionId",
                table: "Apprentices");

            migrationBuilder.DropForeignKey(
                name: "FK_Entries_Sessions_SessionId",
                table: "Entries");

            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS Entries_ai;
                DROP TRIGGER IF EXISTS Entries_ad;
                DROP TRIGGER IF EXISTS Entries_au;
                DROP TABLE IF EXISTS Entries_fts;
                """);

            migrationBuilder.DropIndex(
                name: "IX_Sessions_Status",
                table: "Sessions");

            migrationBuilder.DropIndex(
                name: "IX_Sessions_UpdatedAt",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "CampaignId",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "ToolArguments",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "ToolCallId",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "ToolName",
                table: "Entries");

            migrationBuilder.AlterColumn<string>(
                name: "Title",
                table: "Sessions",
                type: "TEXT",
                maxLength: 512,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 512,
                oldNullable: true);

            migrationBuilder.RenameIndex(
                name: "IX_Apprentices_SessionId",
                table: "Apprentices",
                newName: "IX_Apprentices_ConversationId");

            migrationBuilder.RenameColumn(
                name: "SessionId",
                table: "Apprentices",
                newName: "ConversationId");

            migrationBuilder.RenameIndex(
                name: "IX_Entries_SessionId_CreatedAt",
                table: "Entries",
                newName: "IX_ChatMessages_ConversationId_Timestamp");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "Entries",
                newName: "Timestamp");

            migrationBuilder.RenameColumn(
                name: "SessionId",
                table: "Entries",
                newName: "ConversationId");

            migrationBuilder.RenameIndex(
                name: "IX_Sessions_CreatedAt",
                table: "Sessions",
                newName: "IX_Conversations_CreatedAt");

            migrationBuilder.RenameTable(
                name: "Entries",
                newName: "ChatMessages");

            migrationBuilder.RenameTable(
                name: "Sessions",
                newName: "Conversations");

            migrationBuilder.AddForeignKey(
                name: "FK_ChatMessages_Conversations_ConversationId",
                table: "ChatMessages",
                column: "ConversationId",
                principalTable: "Conversations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Apprentices_Conversations_ConversationId",
                table: "Apprentices",
                column: "ConversationId",
                principalTable: "Conversations",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.Sql(
                """
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
                """);
        }
    }
}
