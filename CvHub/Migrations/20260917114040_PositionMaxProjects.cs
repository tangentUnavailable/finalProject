using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CvHub.Migrations
{
    /// <inheritdoc />
    public partial class PositionMaxProjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxProjects",
                table: "Positions",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxProjects",
                table: "Positions");
        }
    }
}
