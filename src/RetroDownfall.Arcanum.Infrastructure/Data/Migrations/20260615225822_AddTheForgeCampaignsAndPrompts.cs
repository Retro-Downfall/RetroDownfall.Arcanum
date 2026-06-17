using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RetroDownfall.Arcanum.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTheForgeCampaignsAndPrompts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Campaigns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    NameLower = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Settings = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Campaigns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Prompts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CampaignId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Tags = table.Column<string>(type: "TEXT", nullable: false),
                    Template = table.Column<string>(type: "TEXT", nullable: false),
                    ParameterSchema = table.Column<string>(type: "TEXT", nullable: true),
                    DefaultParameters = table.Column<string>(type: "TEXT", nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Temperature = table.Column<double>(type: "REAL", nullable: true),
                    TopP = table.Column<double>(type: "REAL", nullable: true),
                    MaxOutputTokens = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Prompts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Prompts_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_NameLower",
                table: "Campaigns",
                column: "NameLower",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_Path",
                table: "Campaigns",
                column: "Path",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_Type",
                table: "Campaigns",
                column: "Type");

            migrationBuilder.CreateIndex(
                name: "IX_Prompts_CampaignId_Name",
                table: "Prompts",
                columns: new[] { "CampaignId", "Name" });

            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX IX_Prompts_Name_Version_Global ON Prompts(Name, Version) WHERE CampaignId IS NULL;
                CREATE UNIQUE INDEX IX_Prompts_Name_Version_Campaign ON Prompts(Name, Version, CampaignId) WHERE CampaignId IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Prompts");

            migrationBuilder.DropTable(
                name: "Campaigns");
        }
    }
}
