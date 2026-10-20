using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaMtxPanel.Data.Migrations
{
    /// <inheritdoc />
    public partial class EmbedPlayerAndTicketCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Code",
                table: "Orders",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RedeemBefore",
                table: "Orders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Embeds",
                columns: table => new
                {
                    StreamId = table.Column<int>(type: "INTEGER", nullable: false),
                    LookJson = table.Column<string>(type: "TEXT", nullable: false),
                    LogoData = table.Column<byte[]>(type: "BLOB", nullable: true),
                    LogoContentType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    PosterData = table.Column<byte[]>(type: "BLOB", nullable: true),
                    PosterContentType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    AllowedSites = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Embeds", x => x.StreamId);
                    table.ForeignKey(
                        name: "FK_Embeds_Streams_StreamId",
                        column: x => x.StreamId,
                        principalTable: "Streams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ViewerLoginCodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CodeHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    Used = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ViewerLoginCodes", x => x.Id);
                });

            // Viewers sign in with their e-mail address; it is now stored in lowercase.
            migrationBuilder.Sql("UPDATE Orders SET BuyerEmail = lower(trim(BuyerEmail)) WHERE BuyerEmail IS NOT NULL;");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_BuyerEmail",
                table: "Orders",
                column: "BuyerEmail");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Code",
                table: "Orders",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ViewerLoginCodes_Email_CreatedAt",
                table: "ViewerLoginCodes",
                columns: new[] { "Email", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Embeds");

            migrationBuilder.DropTable(
                name: "ViewerLoginCodes");

            migrationBuilder.DropIndex(
                name: "IX_Orders_BuyerEmail",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_Code",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "Code",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "RedeemBefore",
                table: "Orders");
        }
    }
}
