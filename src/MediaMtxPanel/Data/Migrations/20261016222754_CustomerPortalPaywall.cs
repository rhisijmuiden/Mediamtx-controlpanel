using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaMtxPanel.Data.Migrations
{
    /// <inheritdoc />
    public partial class CustomerPortalPaywall : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PaywallEnabled",
                table: "Streams",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WatchSlug",
                table: "Streams",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MetricsUrl",
                table: "Servers",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Offers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StreamId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    PriceCents = table.Column<int>(type: "INTEGER", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    AccessHours = table.Column<int>(type: "INTEGER", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Offers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Offers_Streams_StreamId",
                        column: x => x.StreamId,
                        principalTable: "Streams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PaymentAccounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    MollieEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    MollieApiKeyProtected = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    PayPalEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    PayPalClientId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    PayPalSecretProtected = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    PayPalSandbox = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentAccounts_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StreamId = table.Column<int>(type: "INTEGER", nullable: false),
                    OfferId = table.Column<int>(type: "INTEGER", nullable: true),
                    OfferName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    AmountCents = table.Column<int>(type: "INTEGER", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    AccessHours = table.Column<int>(type: "INTEGER", nullable: false),
                    BuyerEmail = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderPaymentId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    AccessToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PaidAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AccessExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Orders_Offers_OfferId",
                        column: x => x.OfferId,
                        principalTable: "Offers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Orders_Streams_StreamId",
                        column: x => x.StreamId,
                        principalTable: "Streams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Existing streams get a random watch-page slug before the unique index is created.
            migrationBuilder.Sql("UPDATE Streams SET WatchSlug = lower(hex(randomblob(8))) WHERE WatchSlug = '';");

            migrationBuilder.CreateIndex(
                name: "IX_Streams_WatchSlug",
                table: "Streams",
                column: "WatchSlug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Offers_StreamId",
                table: "Offers",
                column: "StreamId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_AccessToken",
                table: "Orders",
                column: "AccessToken");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_OfferId",
                table: "Orders",
                column: "OfferId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_ProviderPaymentId",
                table: "Orders",
                column: "ProviderPaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_StreamId",
                table: "Orders",
                column: "StreamId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAccounts_UserId",
                table: "PaymentAccounts",
                column: "UserId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Orders");

            migrationBuilder.DropTable(
                name: "PaymentAccounts");

            migrationBuilder.DropTable(
                name: "Offers");

            migrationBuilder.DropIndex(
                name: "IX_Streams_WatchSlug",
                table: "Streams");

            migrationBuilder.DropColumn(
                name: "PaywallEnabled",
                table: "Streams");

            migrationBuilder.DropColumn(
                name: "WatchSlug",
                table: "Streams");

            migrationBuilder.DropColumn(
                name: "MetricsUrl",
                table: "Servers");
        }
    }
}
