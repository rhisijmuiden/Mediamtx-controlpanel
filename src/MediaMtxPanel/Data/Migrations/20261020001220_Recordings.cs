using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaMtxPanel.Data.Migrations
{
    /// <inheritdoc />
    public partial class Recordings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RecordRetentionDays",
                table: "Streams",
                type: "INTEGER",
                nullable: false,
                // Existing streams keep what MediaMTX did before the panel managed retention: one day.
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "PlaybackUrl",
                table: "Servers",
                type: "TEXT",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecordRetentionDays",
                table: "Streams");

            migrationBuilder.DropColumn(
                name: "PlaybackUrl",
                table: "Servers");
        }
    }
}
