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
                name: "AppSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SynologyBaseUrl = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SynologyUsername = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SynologyPasswordEncrypted = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    SynologyOtpCode = table.Column<string>(type: "TEXT", nullable: true),
                    SynologyMovieDestination = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SynologySeriesDestination = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ScanIntervalMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    ScanOnStartup = table.Column<bool>(type: "INTEGER", nullable: false),
                    HosterPriorityCsv = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    AuthUsername = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AuthPasswordEncrypted = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    TmdbApiKeyEncrypted = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    TmdbEnrichmentEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    TmdbEnrichmentConcurrency = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Id);
                });

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
                name: "CatalogImportRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SourceDescription = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    TotalRecords = table.Column<long>(type: "INTEGER", nullable: false),
                    InsertedLinks = table.Column<long>(type: "INTEGER", nullable: false),
                    InsertedTitles = table.Column<long>(type: "INTEGER", nullable: false),
                    InsertedEpisodes = table.Column<long>(type: "INTEGER", nullable: false),
                    FailedRecords = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogImportRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CatalogTitles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CanonicalKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    TitleName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    OriginalTitle = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    NormalizedTitle = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ImdbId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    TmdbId = table.Column<int>(type: "INTEGER", nullable: true),
                    CategoryName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TitlePoster = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    FirstSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LinkCount = table.Column<int>(type: "INTEGER", nullable: false),
                    EpisodeCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogTitles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScanRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceId = table.Column<string>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FinishedAt = table.Column<long>(type: "INTEGER", nullable: true),
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
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Titles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CatalogEpisodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TitleId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    EpisodeNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    EpisodeName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    EpisodePoster = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    IsFullSeason = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogEpisodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CatalogEpisodes_CatalogTitles_TitleId",
                        column: x => x.TitleId,
                        principalTable: "CatalogTitles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CatalogTitleMetadata",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TitleId = table.Column<int>(type: "INTEGER", nullable: false),
                    TmdbId = table.Column<int>(type: "INTEGER", nullable: true),
                    ImdbId = table.Column<string>(type: "TEXT", nullable: true),
                    ReleaseDate = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    Runtime = table.Column<int>(type: "INTEGER", nullable: true),
                    VoteAverage = table.Column<double>(type: "REAL", nullable: true),
                    VoteCount = table.Column<int>(type: "INTEGER", nullable: true),
                    Popularity = table.Column<double>(type: "REAL", nullable: true),
                    GenresJson = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalLanguage = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    Overview = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    EnrichmentSource = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    LastEnrichedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogTitleMetadata", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CatalogTitleMetadata_CatalogTitles_TitleId",
                        column: x => x.TitleId,
                        principalTable: "CatalogTitles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                    AggregatorDlProtectUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    HostersJson = table.Column<string>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    FailureReason = table.Column<string>(type: "TEXT", nullable: true),
                    ResolutionAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    DiscoveredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ResolvedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    SubmittedAt = table.Column<long>(type: "INTEGER", nullable: true)
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
                name: "CatalogLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ExternalLinkId = table.Column<long>(type: "INTEGER", nullable: false),
                    TitleId = table.Column<int>(type: "INTEGER", nullable: false),
                    EpisodeId = table.Column<int>(type: "INTEGER", nullable: true),
                    LinkUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    HostName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    NormalizedHost = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    QualityName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    AudioLangs = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    SubLangs = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CatalogLinks_CatalogEpisodes_EpisodeId",
                        column: x => x.EpisodeId,
                        principalTable: "CatalogEpisodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CatalogLinks_CatalogTitles_TitleId",
                        column: x => x.TitleId,
                        principalTable: "CatalogTitles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                    ResolvedAt = table.Column<long>(type: "INTEGER", nullable: false)
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
                    SubmittedAt = table.Column<long>(type: "INTEGER", nullable: false)
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
                name: "IX_CatalogEpisodes_TitleId_SeasonNumber_EpisodeNumber_IsFullSeason",
                table: "CatalogEpisodes",
                columns: new[] { "TitleId", "SeasonNumber", "EpisodeNumber", "IsFullSeason" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CatalogImportRuns_StartedAt",
                table: "CatalogImportRuns",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogLinks_EpisodeId",
                table: "CatalogLinks",
                column: "EpisodeId");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogLinks_ExternalLinkId",
                table: "CatalogLinks",
                column: "ExternalLinkId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CatalogLinks_NormalizedHost",
                table: "CatalogLinks",
                column: "NormalizedHost");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogLinks_QualityName",
                table: "CatalogLinks",
                column: "QualityName");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogLinks_TitleId",
                table: "CatalogLinks",
                column: "TitleId");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogTitleMetadata_EnrichmentSource",
                table: "CatalogTitleMetadata",
                column: "EnrichmentSource");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogTitleMetadata_TitleId",
                table: "CatalogTitleMetadata",
                column: "TitleId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CatalogTitleMetadata_Year",
                table: "CatalogTitleMetadata",
                column: "Year");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogTitles_CanonicalKey",
                table: "CatalogTitles",
                column: "CanonicalKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CatalogTitles_CategoryName",
                table: "CatalogTitles",
                column: "CategoryName");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogTitles_ImdbId",
                table: "CatalogTitles",
                column: "ImdbId");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogTitles_NormalizedTitle",
                table: "CatalogTitles",
                column: "NormalizedTitle");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogTitles_TmdbId",
                table: "CatalogTitles",
                column: "TmdbId");

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
                name: "AppSettings");

            migrationBuilder.DropTable(
                name: "CapSolverSpends");

            migrationBuilder.DropTable(
                name: "CatalogImportRuns");

            migrationBuilder.DropTable(
                name: "CatalogLinks");

            migrationBuilder.DropTable(
                name: "CatalogTitleMetadata");

            migrationBuilder.DropTable(
                name: "ResolvedLinks");

            migrationBuilder.DropTable(
                name: "ScanRuns");

            migrationBuilder.DropTable(
                name: "Submissions");

            migrationBuilder.DropTable(
                name: "CatalogEpisodes");

            migrationBuilder.DropTable(
                name: "Articles");

            migrationBuilder.DropTable(
                name: "CatalogTitles");

            migrationBuilder.DropTable(
                name: "Titles");
        }
    }
}
