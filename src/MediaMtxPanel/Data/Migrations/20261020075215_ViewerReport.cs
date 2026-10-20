using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaMtxPanel.Data.Migrations
{
    /// <inheritdoc />
    public partial class ViewerReport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NewViewers",
                table: "StatSamples",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ViewerDailyStats",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Day = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    StreamId = table.Column<int>(type: "INTEGER", nullable: false),
                    StreamName = table.Column<string>(type: "TEXT", nullable: false),
                    PeakViewers = table.Column<int>(type: "INTEGER", nullable: false),
                    UniqueViewers = table.Column<int>(type: "INTEGER", nullable: false),
                    Sessions = table.Column<int>(type: "INTEGER", nullable: false),
                    ViewerSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    LiveSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    BytesIn = table.Column<long>(type: "INTEGER", nullable: false),
                    BytesOut = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ViewerDailyStats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ViewerVisits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Day = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    StreamId = table.Column<int>(type: "INTEGER", nullable: false),
                    ViewerKey = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ViewerVisits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ViewerDailyStats_Day",
                table: "ViewerDailyStats",
                column: "Day");

            migrationBuilder.CreateIndex(
                name: "IX_ViewerDailyStats_StreamId_Day",
                table: "ViewerDailyStats",
                columns: new[] { "StreamId", "Day" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ViewerVisits_Day_StreamId_ViewerKey",
                table: "ViewerVisits",
                columns: new[] { "Day", "StreamId", "ViewerKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ViewerVisits_StreamId_Day",
                table: "ViewerVisits",
                columns: new[] { "StreamId", "Day" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ViewerDailyStats");

            migrationBuilder.DropTable(
                name: "ViewerVisits");

            migrationBuilder.DropColumn(
                name: "NewViewers",
                table: "StatSamples");
        }
    }
}
