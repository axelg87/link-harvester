using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkHarvester.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogBrowseIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Titles_Status_UpdatedAt",
                table: "Titles",
                columns: new[] { "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogTitleMetadata_Popularity",
                table: "CatalogTitleMetadata",
                column: "Popularity");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogTitleMetadata_VoteAverage",
                table: "CatalogTitleMetadata",
                column: "VoteAverage");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Titles_Status_UpdatedAt",
                table: "Titles");

            migrationBuilder.DropIndex(
                name: "IX_CatalogTitleMetadata_Popularity",
                table: "CatalogTitleMetadata");

            migrationBuilder.DropIndex(
                name: "IX_CatalogTitleMetadata_VoteAverage",
                table: "CatalogTitleMetadata");
        }
    }
}
