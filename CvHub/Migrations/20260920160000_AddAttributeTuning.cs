using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CvHub.Migrations;

/// <inheritdoc />
[Migration("20260920160000_AddAttributeTuning")]
public partial class AddAttributeTuning : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Optional attribute "tuning" (req #4): length / regex / numeric-range constraints on AttributeDef.
        // All nullable so existing rows + the design-time snapshot aren't broken. EF records this migration in
        // __EFMigrationsHistory, so Up runs once at startup via Database.Migrate() (no IF NOT EXISTS needed).
        migrationBuilder.AddColumn<int>("MinLength", "Attributes", type: "integer", nullable: true);
        migrationBuilder.AddColumn<int>("MaxLength", "Attributes", type: "integer", nullable: true);
        migrationBuilder.AddColumn<string>("RegexPattern", "Attributes", type: "text", nullable: true);
        migrationBuilder.AddColumn<double>("MinValue", "Attributes", type: "double precision", nullable: true);
        migrationBuilder.AddColumn<double>("MaxValue", "Attributes", type: "double precision", nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("MaxValue", "Attributes");
        migrationBuilder.DropColumn("MinValue", "Attributes");
        migrationBuilder.DropColumn("RegexPattern", "Attributes");
        migrationBuilder.DropColumn("MaxLength", "Attributes");
        migrationBuilder.DropColumn("MinLength", "Attributes");
    }
}
