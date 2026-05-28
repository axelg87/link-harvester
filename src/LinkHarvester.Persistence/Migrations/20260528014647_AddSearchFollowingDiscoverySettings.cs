using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkHarvester.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchFollowingDiscoverySettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastAirDate",
                table: "CatalogTitleMetadata",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "NextEpisodeAirDate",
                table: "CatalogTitleMetadata",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NextEpisodeCode",
                table: "CatalogTitleMetadata",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AudioPreference",
                table: "AppSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "QualityPreferenceCsv",
                table: "AppSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TelegramBotTokenEncrypted",
                table: "AppSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "TelegramOwnerChatId",
                table: "AppSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "TraktClientIdEncrypted",
                table: "AppSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "DiscoveryEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CatalogTitleId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    Rank = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    FetchedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveryEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FollowingDetectionLog",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CatalogTitleId = table.Column<int>(type: "INTEGER", nullable: false),
                    EpisodeCode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ArticleId = table.Column<int>(type: "INTEGER", nullable: true),
                    CatalogLinkId = table.Column<int>(type: "INTEGER", nullable: true),
                    DetectedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ConsumedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ConsumedBy = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FollowingDetectionLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FollowingDismissals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CatalogTitleId = table.Column<int>(type: "INTEGER", nullable: false),
                    DismissedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FollowingDismissals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryEntries_CatalogTitleId_Source",
                table: "DiscoveryEntries",
                columns: new[] { "CatalogTitleId", "Source" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryEntries_FetchedAt",
                table: "DiscoveryEntries",
                column: "FetchedAt");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveryEntries_Source_Rank",
                table: "DiscoveryEntries",
                columns: new[] { "Source", "Rank" });

            migrationBuilder.CreateIndex(
                name: "IX_FollowingDetectionLog_CatalogTitleId",
                table: "FollowingDetectionLog",
                column: "CatalogTitleId");

            migrationBuilder.CreateIndex(
                name: "IX_FollowingDetectionLog_CatalogTitleId_EpisodeCode",
                table: "FollowingDetectionLog",
                columns: new[] { "CatalogTitleId", "EpisodeCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FollowingDetectionLog_ConsumedAt_DetectedAt",
                table: "FollowingDetectionLog",
                columns: new[] { "ConsumedAt", "DetectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FollowingDismissals_CatalogTitleId",
                table: "FollowingDismissals",
                column: "CatalogTitleId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscoveryEntries");

            migrationBuilder.DropTable(
                name: "FollowingDetectionLog");

            migrationBuilder.DropTable(
                name: "FollowingDismissals");

            migrationBuilder.DropColumn(
                name: "LastAirDate",
                table: "CatalogTitleMetadata");

            migrationBuilder.DropColumn(
                name: "NextEpisodeAirDate",
                table: "CatalogTitleMetadata");

            migrationBuilder.DropColumn(
                name: "NextEpisodeCode",
                table: "CatalogTitleMetadata");

            migrationBuilder.DropColumn(
                name: "AudioPreference",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "QualityPreferenceCsv",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "TelegramBotTokenEncrypted",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "TelegramOwnerChatId",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "TraktClientIdEncrypted",
                table: "AppSettings");
        }
    }
}
