using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RetroDownfall.Arcanum.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionQueryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Sessions_CampaignId",
                table: "Sessions",
                column: "CampaignId");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_Status_UpdatedAt",
                table: "Sessions",
                columns: new[] { "Status", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sessions_CampaignId",
                table: "Sessions");

            migrationBuilder.DropIndex(
                name: "IX_Sessions_Status_UpdatedAt",
                table: "Sessions");
        }
    }
}
