using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkHarvester.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCatalogCardLinkFacetsAudioLangsIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_CatalogCardLinkFacets_AudioLangs_CardId",
                table: "CatalogCardLinkFacets",
                columns: new[] { "AudioLangs", "CardId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CatalogCardLinkFacets_AudioLangs_CardId",
                table: "CatalogCardLinkFacets");
        }
    }
}
