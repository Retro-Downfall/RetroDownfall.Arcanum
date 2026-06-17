using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RetroDownfall.Arcanum.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCampaignSanctumConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SanctumConfigJson",
                table: "Campaigns",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SanctumConfigJson",
                table: "Campaigns");
        }
    }
}
