using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkHarvester.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CapSolverSpends",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Year = table.Column<int>(type: "INTEGER", nullable: false),
                    Month = table.Column<int>(type: "INTEGER", nullable: false),
                    SpentUsd = table.Column<decimal>(type: "TEXT", nullable: false),
                    Calls = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CapSolverSpends", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScanRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceId = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Discovered = table.Column<int>(type: "INTEGER", nullable: false),
                    NewArticles = table.Column<int>(type: "INTEGER", nullable: false),
                    Resolved = table.Column<int>(type: "INTEGER", nullable: false),
                    Failed = table.Column<int>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Titles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Canonical = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    NormalizedTitle = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    DisplayTitle = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    SeasonNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    BestSubmittedQualityScore = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Titles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Articles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TitleId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    DisplayTitle = table.Column<string>(type: "TEXT", nullable: false),
                    QualityLabel = table.Column<string>(type: "TEXT", nullable: true),
                    QualityScore = table.Column<int>(type: "INTEGER", nullable: false),
                    Resolution = table.Column<string>(type: "TEXT", nullable: true),
                    Codec = table.Column<string>(type: "TEXT", nullable: true),
                    SourceTier = table.Column<string>(type: "TEXT", nullable: true),
                    Language = table.Column<string>(type: "TEXT", nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    EpisodeCount = table.Column<int>(type: "INTEGER", nullable: true),
                    DlProtectUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    HostersJson = table.Column<string>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    FailureReason = table.Column<string>(type: "TEXT", nullable: true),
                    ResolutionAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    DiscoveredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Articles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Articles_Titles_TitleId",
                        column: x => x.TitleId,
                        principalTable: "Titles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ResolvedLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ArticleId = table.Column<int>(type: "INTEGER", nullable: false),
                    Hoster = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    EpisodeIndex = table.Column<int>(type: "INTEGER", nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResolvedLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResolvedLinks_Articles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "Articles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Submissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ArticleId = table.Column<int>(type: "INTEGER", nullable: false),
                    SubmittedUrlsJson = table.Column<string>(type: "TEXT", nullable: false),
                    DsmTaskIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ResponseMessage = table.Column<string>(type: "TEXT", nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Submissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Submissions_Articles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "Articles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Articles_SourceId_ExternalId",
                table: "Articles",
                columns: new[] { "SourceId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Articles_Status",
                table: "Articles",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Articles_TitleId",
                table: "Articles",
                column: "TitleId");

            migrationBuilder.CreateIndex(
                name: "IX_CapSolverSpends_Year_Month",
                table: "CapSolverSpends",
                columns: new[] { "Year", "Month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResolvedLinks_ArticleId",
                table: "ResolvedLinks",
                column: "ArticleId");

            migrationBuilder.CreateIndex(
                name: "IX_ScanRuns_SourceId_StartedAt",
                table: "ScanRuns",
                columns: new[] { "SourceId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_ArticleId",
                table: "Submissions",
                column: "ArticleId");

            migrationBuilder.CreateIndex(
                name: "IX_Titles_Canonical",
                table: "Titles",
                column: "Canonical",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Titles_NormalizedTitle_Year_Kind_SeasonNumber",
                table: "Titles",
                columns: new[] { "NormalizedTitle", "Year", "Kind", "SeasonNumber" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CapSolverSpends");

            migrationBuilder.DropTable(
                name: "ResolvedLinks");

            migrationBuilder.DropTable(
                name: "ScanRuns");

            migrationBuilder.DropTable(
                name: "Submissions");

            migrationBuilder.DropTable(
                name: "Articles");

            migrationBuilder.DropTable(
                name: "Titles");
        }
    }
}
