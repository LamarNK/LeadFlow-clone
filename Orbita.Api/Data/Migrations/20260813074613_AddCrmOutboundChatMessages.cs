using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCrmOutboundChatMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CrmOutboundChatMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CardId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResponseId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AuthorName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeliveryClaimedByWorkerId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    DeliveryClaimedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrmOutboundChatMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CrmOutboundChatMessages_CrmCandidateCards_CardId",
                        column: x => x.CardId,
                        principalTable: "CrmCandidateCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CrmOutboundChatMessages_CardId_CreatedAtUtc",
                table: "CrmOutboundChatMessages",
                columns: new[] { "CardId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CrmOutboundChatMessages_ResponseId_Status_CreatedAtUtc",
                table: "CrmOutboundChatMessages",
                columns: new[] { "ResponseId", "Status", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CrmOutboundChatMessages");
        }
    }
}
