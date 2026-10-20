using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaMtxPanel.Data.Migrations
{
    /// <inheritdoc />
    public partial class StreamStatistics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StatSamples",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StreamId = table.Column<int>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<long>(type: "INTEGER", nullable: false),
                    Seconds = table.Column<int>(type: "INTEGER", nullable: false),
                    Viewers = table.Column<int>(type: "INTEGER", nullable: false),
                    BytesIn = table.Column<long>(type: "INTEGER", nullable: false),
                    BytesOut = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatSamples", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StatSamples_Streams_StreamId",
                        column: x => x.StreamId,
                        principalTable: "Streams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StatSamples_StreamId_Timestamp",
                table: "StatSamples",
                columns: new[] { "StreamId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_StatSamples_Timestamp",
                table: "StatSamples",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StatSamples");
        }
    }
}
