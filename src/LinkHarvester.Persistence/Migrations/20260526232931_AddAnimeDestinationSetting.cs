using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LinkHarvester.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAnimeDestinationSetting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SynologyAnimeDestination",
                table: "AppSettings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SynologyAnimeDestination",
                table: "AppSettings");
        }
    }
}
