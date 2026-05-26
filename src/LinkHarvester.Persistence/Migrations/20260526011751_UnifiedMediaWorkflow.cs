using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkHarvester.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UnifiedMediaWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Submissions_Articles_ArticleId",
                table: "Submissions");

            migrationBuilder.AddColumn<long>(
                name: "CatalogPromotedAt",
                table: "Titles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CatalogTitleId",
                table: "Titles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImdbId",
                table: "Titles",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TmdbId",
                table: "Titles",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "ArticleId",
                table: "Submissions",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AddColumn<int>(
                name: "AttemptNumber",
                table: "Submissions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "CatalogLinkIdsJson",
                table: "Submissions",
                type: "TEXT",
                maxLength: 1024,
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<int>(
                name: "CatalogTitleId",
                table: "Submissions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CompletedAt",
                table: "Submissions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Destination",
                table: "Submissions",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisplayTitle",
                table: "Submissions",
                type: "TEXT",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "DsmErrorCode",
                table: "Submissions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DsmFailedUrl",
                table: "Submissions",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResendOfSubmissionId",
                table: "Submissions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                table: "Submissions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "UrlCount",
                table: "Submissions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "MetadataUncertain",
                table: "CatalogTitleMetadata",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "HarvesterArticleId",
                table: "CatalogLinks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LinkSource",
                table: "CatalogLinks",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "catalog");

            migrationBuilder.Sql(@"
                UPDATE Submissions
                SET DisplayTitle = COALESCE((
                    SELECT Titles.DisplayTitle
                    FROM Articles
                    JOIN Titles ON Titles.Id = Articles.TitleId
                    WHERE Articles.Id = Submissions.ArticleId
                ), ''),
                UrlCount = CASE
                    WHEN SubmittedUrlsJson IS NULL OR SubmittedUrlsJson = '' OR SubmittedUrlsJson = '[]' THEN 0
                    ELSE 1 + LENGTH(SubmittedUrlsJson) - LENGTH(REPLACE(SubmittedUrlsJson, ',', ''))
                END,
                CatalogLinkIdsJson = '[]',
                AttemptNumber = 1,
                Source = 0;");

            migrationBuilder.CreateIndex(
                name: "IX_Titles_CatalogTitleId",
                table: "Titles",
                column: "CatalogTitleId");

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_CatalogTitleId",
                table: "Submissions",
                column: "CatalogTitleId");

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_Source_SubmittedAt",
                table: "Submissions",
                columns: new[] { "Source", "SubmittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_Status_SubmittedAt",
                table: "Submissions",
                columns: new[] { "Status", "SubmittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogLinks_HarvesterArticleId",
                table: "CatalogLinks",
                column: "HarvesterArticleId");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogLinks_LinkSource",
                table: "CatalogLinks",
                column: "LinkSource");

            migrationBuilder.AddForeignKey(
                name: "FK_Submissions_Articles_ArticleId",
                table: "Submissions",
                column: "ArticleId",
                principalTable: "Articles",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Submissions_Articles_ArticleId",
                table: "Submissions");

            migrationBuilder.DropIndex(
                name: "IX_Titles_CatalogTitleId",
                table: "Titles");

            migrationBuilder.DropIndex(
                name: "IX_Submissions_CatalogTitleId",
                table: "Submissions");

            migrationBuilder.DropIndex(
                name: "IX_Submissions_Source_SubmittedAt",
                table: "Submissions");

            migrationBuilder.DropIndex(
                name: "IX_Submissions_Status_SubmittedAt",
                table: "Submissions");

            migrationBuilder.DropIndex(
                name: "IX_CatalogLinks_HarvesterArticleId",
                table: "CatalogLinks");

            migrationBuilder.DropIndex(
                name: "IX_CatalogLinks_LinkSource",
                table: "CatalogLinks");

            migrationBuilder.DropColumn(
                name: "CatalogPromotedAt",
                table: "Titles");

            migrationBuilder.DropColumn(
                name: "CatalogTitleId",
                table: "Titles");

            migrationBuilder.DropColumn(
                name: "ImdbId",
                table: "Titles");

            migrationBuilder.DropColumn(
                name: "TmdbId",
                table: "Titles");

            migrationBuilder.DropColumn(
                name: "AttemptNumber",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "CatalogLinkIdsJson",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "CatalogTitleId",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "Destination",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "DisplayTitle",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "DsmErrorCode",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "DsmFailedUrl",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "ResendOfSubmissionId",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "UrlCount",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "MetadataUncertain",
                table: "CatalogTitleMetadata");

            migrationBuilder.DropColumn(
                name: "HarvesterArticleId",
                table: "CatalogLinks");

            migrationBuilder.DropColumn(
                name: "LinkSource",
                table: "CatalogLinks");

            migrationBuilder.AlterColumn<int>(
                name: "ArticleId",
                table: "Submissions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Submissions_Articles_ArticleId",
                table: "Submissions",
                column: "ArticleId",
                principalTable: "Articles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
