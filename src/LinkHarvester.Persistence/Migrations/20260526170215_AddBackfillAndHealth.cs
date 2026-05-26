using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkHarvester.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBackfillAndHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HiddenReason",
                table: "CatalogTitles",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsHidden",
                table: "CatalogTitles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "HealthCheckedAt",
                table: "CatalogLinks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HealthSignature",
                table: "CatalogLinks",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HealthStatus",
                table: "CatalogLinks",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BackfillRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    FromDate = table.Column<long>(type: "INTEGER", nullable: false),
                    StartPage = table.Column<int>(type: "INTEGER", nullable: false),
                    LastCompletedPage = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSeenArticleExternalId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Discovered = table.Column<int>(type: "INTEGER", nullable: false),
                    Healthy = table.Column<int>(type: "INTEGER", nullable: false),
                    Enriched = table.Column<int>(type: "INTEGER", nullable: false),
                    Promoted = table.Column<int>(type: "INTEGER", nullable: false),
                    Skipped = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackfillRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HealthSweepRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastCheckedCatalogLinkId = table.Column<int>(type: "INTEGER", nullable: false),
                    Checked = table.Column<int>(type: "INTEGER", nullable: false),
                    Alive = table.Column<int>(type: "INTEGER", nullable: false),
                    Dead = table.Column<int>(type: "INTEGER", nullable: false),
                    Unknown = table.Column<int>(type: "INTEGER", nullable: false),
                    HiddenTitles = table.Column<int>(type: "INTEGER", nullable: false),
                    HosterFilter = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthSweepRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogTitles_IsHidden",
                table: "CatalogTitles",
                column: "IsHidden");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogLinks_HealthStatus",
                table: "CatalogLinks",
                column: "HealthStatus");

            migrationBuilder.CreateIndex(
                name: "IX_BackfillRuns_SourceId_Kind_StartedAt",
                table: "BackfillRuns",
                columns: new[] { "SourceId", "Kind", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_HealthSweepRuns_StartedAt",
                table: "HealthSweepRuns",
                column: "StartedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackfillRuns");

            migrationBuilder.DropTable(
                name: "HealthSweepRuns");

            migrationBuilder.DropIndex(
                name: "IX_CatalogTitles_IsHidden",
                table: "CatalogTitles");

            migrationBuilder.DropIndex(
                name: "IX_CatalogLinks_HealthStatus",
                table: "CatalogLinks");

            migrationBuilder.DropColumn(
                name: "HiddenReason",
                table: "CatalogTitles");

            migrationBuilder.DropColumn(
                name: "IsHidden",
                table: "CatalogTitles");

            migrationBuilder.DropColumn(
                name: "HealthCheckedAt",
                table: "CatalogLinks");

            migrationBuilder.DropColumn(
                name: "HealthSignature",
                table: "CatalogLinks");

            migrationBuilder.DropColumn(
                name: "HealthStatus",
                table: "CatalogLinks");
        }
    }
}
