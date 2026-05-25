using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkHarvester.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSynologyQuickConnect : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SynologyConnectionMode",
                table: "AppSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SynologyQuickConnectId",
                table: "AppSettings",
                type: "TEXT",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SynologyResolvedBaseUrl",
                table: "AppSettings",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "SynologyResolvedAt",
                table: "AppSettings",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SynologyConnectionMode",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "SynologyQuickConnectId",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "SynologyResolvedBaseUrl",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "SynologyResolvedAt",
                table: "AppSettings");
        }
    }
}
