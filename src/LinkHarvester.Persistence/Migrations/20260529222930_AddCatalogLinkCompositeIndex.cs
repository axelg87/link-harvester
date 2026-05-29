using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkHarvester.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogLinkCompositeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_CatalogLinks_TitleId_NormalizedHost_QualityName",
                table: "CatalogLinks",
                columns: new[] { "TitleId", "NormalizedHost", "QualityName" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CatalogLinks_TitleId_NormalizedHost_QualityName",
                table: "CatalogLinks");
        }
    }
}
