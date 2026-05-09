using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RetroDownfall.Arcanum.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class EvolveWorkspaceContextForChronosync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkspaceContexts_RootPath",
                table: "WorkspaceContexts");

            migrationBuilder.RenameColumn(
                name: "ProjectSummary",
                table: "WorkspaceContexts",
                newName: "SerializedSnapshot");

            migrationBuilder.RenameColumn(
                name: "LastScanned",
                table: "WorkspaceContexts",
                newName: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceContexts_RootPath_CreatedAt",
                table: "WorkspaceContexts",
                columns: new[] { "RootPath", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkspaceContexts_RootPath_CreatedAt",
                table: "WorkspaceContexts");

            migrationBuilder.RenameColumn(
                name: "SerializedSnapshot",
                table: "WorkspaceContexts",
                newName: "ProjectSummary");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "WorkspaceContexts",
                newName: "LastScanned");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceContexts_RootPath",
                table: "WorkspaceContexts",
                column: "RootPath");
        }
    }
}
