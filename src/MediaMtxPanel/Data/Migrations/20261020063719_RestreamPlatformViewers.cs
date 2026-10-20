using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaMtxPanel.Data.Migrations
{
    /// <inheritdoc />
    public partial class RestreamPlatformViewers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PlatformViewers",
                table: "StatSamples",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ViewerChannel",
                table: "RestreamTargets",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ViewerClientId",
                table: "RestreamTargets",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ViewerSecretProtected",
                table: "RestreamTargets",
                type: "TEXT",
                maxLength: 4096,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PlatformViewers",
                table: "StatSamples");

            migrationBuilder.DropColumn(
                name: "ViewerChannel",
                table: "RestreamTargets");

            migrationBuilder.DropColumn(
                name: "ViewerClientId",
                table: "RestreamTargets");

            migrationBuilder.DropColumn(
                name: "ViewerSecretProtected",
                table: "RestreamTargets");
        }
    }
}
