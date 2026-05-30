using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkHarvester.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCardReadModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CardsBackfilledAt",
                table: "AppSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CatalogCardGenres",
                columns: table => new
                {
                    CardId = table.Column<int>(type: "INTEGER", nullable: false),
                    Genre = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogCardGenres", x => new { x.CardId, x.Genre });
                });

            migrationBuilder.CreateTable(
                name: "CatalogCardLinkFacets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CardId = table.Column<int>(type: "INTEGER", nullable: false),
                    NormalizedHost = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    QualityName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    AudioLangs = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogCardLinkFacets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CatalogCards",
                columns: table => new
                {
                    TitleId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TitleName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    OriginalTitle = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    NormalizedTitle = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    CategoryName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TitlePoster = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    LinkCount = table.Column<int>(type: "INTEGER", nullable: false),
                    EpisodeCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    Rating = table.Column<double>(type: "REAL", nullable: true),
                    Runtime = table.Column<int>(type: "INTEGER", nullable: true),
                    Overview = table.Column<string>(type: "TEXT", nullable: true),
                    GenresJson = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalLanguage = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    EnrichmentSource = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    MetadataUncertain = table.Column<bool>(type: "INTEGER", nullable: false),
                    HasMetadata = table.Column<bool>(type: "INTEGER", nullable: false),
                    SeasonMin = table.Column<int>(type: "INTEGER", nullable: true),
                    SeasonMax = table.Column<int>(type: "INTEGER", nullable: true),
                    Popularity = table.Column<double>(type: "REAL", nullable: true),
                    IsHidden = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogCards", x => x.TitleId);
                });

            migrationBuilder.CreateTable(
                name: "InboxCards",
                columns: table => new
                {
                    TitleId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CatalogTitleId = table.Column<int>(type: "INTEGER", nullable: true),
                    DisplayTitle = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Poster = table.Column<string>(type: "TEXT", nullable: true),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    Rating = table.Column<double>(type: "REAL", nullable: true),
                    GenresJson = table.Column<string>(type: "TEXT", nullable: false),
                    MetadataUncertain = table.Column<bool>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SeasonNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    BetterAvailable = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ChosenArticleJson = table.Column<string>(type: "TEXT", nullable: false),
                    OtherVariantsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Visible = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxCards", x => x.TitleId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogCardGenres_Genre_CardId",
                table: "CatalogCardGenres",
                columns: new[] { "Genre", "CardId" });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogCardLinkFacets_CardId_NormalizedHost_QualityName",
                table: "CatalogCardLinkFacets",
                columns: new[] { "CardId", "NormalizedHost", "QualityName" });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogCardLinkFacets_NormalizedHost_CardId",
                table: "CatalogCardLinkFacets",
                columns: new[] { "NormalizedHost", "CardId" });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogCardLinkFacets_QualityName_CardId",
                table: "CatalogCardLinkFacets",
                columns: new[] { "QualityName", "CardId" });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogCards_CategoryName",
                table: "CatalogCards",
                column: "CategoryName");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogCards_HasMetadata",
                table: "CatalogCards",
                column: "HasMetadata");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogCards_IsHidden_NormalizedTitle",
                table: "CatalogCards",
                columns: new[] { "IsHidden", "NormalizedTitle" });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogCards_IsHidden_Popularity",
                table: "CatalogCards",
                columns: new[] { "IsHidden", "Popularity" });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogCards_IsHidden_Rating",
                table: "CatalogCards",
                columns: new[] { "IsHidden", "Rating" });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogCards_IsHidden_Year",
                table: "CatalogCards",
                columns: new[] { "IsHidden", "Year" });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogCards_OriginalLanguage",
                table: "CatalogCards",
                column: "OriginalLanguage");

            migrationBuilder.CreateIndex(
                name: "IX_InboxCards_Visible_UpdatedAt",
                table: "InboxCards",
                columns: new[] { "Visible", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CatalogCardGenres");

            migrationBuilder.DropTable(
                name: "CatalogCardLinkFacets");

            migrationBuilder.DropTable(
                name: "CatalogCards");

            migrationBuilder.DropTable(
                name: "InboxCards");

            migrationBuilder.DropColumn(
                name: "CardsBackfilledAt",
                table: "AppSettings");
        }
    }
}
