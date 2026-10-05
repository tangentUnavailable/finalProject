using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CvHub.Migrations
{
    /// <summary>
    /// Maps the already-existing <c>Positions.search_vector</c> generated column into the EF model
    /// (see Position.SearchVector) so full-text queries can target the GIN-indexed column directly.
    /// The column itself (and its GIN index) was created by raw SQL in InitialPostgres — a
    /// GENERATED ALWAYS ... STORED column cannot be re-added here, so this migration is a no-op.
    /// </summary>
    public partial class MapPositionSearchVector : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No-op: search_vector already exists (InitialPostgres raw SQL).
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op.
        }
    }
}
