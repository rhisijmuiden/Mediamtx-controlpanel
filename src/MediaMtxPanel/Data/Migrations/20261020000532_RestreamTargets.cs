using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaMtxPanel.Data.Migrations
{
    /// <inheritdoc />
    public partial class RestreamTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RestreamTargets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StreamId = table.Column<int>(type: "INTEGER", nullable: false),
                    Platform = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ServerUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    StreamKeyProtected = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    TranscodeVideo = table.Column<bool>(type: "INTEGER", nullable: false),
                    VideoBitrateKbps = table.Column<int>(type: "INTEGER", nullable: false),
                    AudioBitrateKbps = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RestreamTargets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RestreamTargets_Streams_StreamId",
                        column: x => x.StreamId,
                        principalTable: "Streams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RestreamTargets_StreamId",
                table: "RestreamTargets",
                column: "StreamId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RestreamTargets");
        }
    }
}
