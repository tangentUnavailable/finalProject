using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CvHub.Migrations
{
    /// <inheritdoc />
    public partial class ScopeApiTokensToPosition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PositionId",
                table: "ExternalApiTokens",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalApiTokens_PositionId",
                table: "ExternalApiTokens",
                column: "PositionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ExternalApiTokens_PositionId",
                table: "ExternalApiTokens");

            migrationBuilder.DropColumn(
                name: "PositionId",
                table: "ExternalApiTokens");
        }
    }
}
