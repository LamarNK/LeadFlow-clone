using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class IndexCrmDeliveriesOutcomeCreatedAtUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ResponseCrmDeliveries_Outcome_CreatedAtUtc",
                table: "ResponseCrmDeliveries",
                columns: new[] { "Outcome", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ResponseCrmDeliveries_Outcome_CreatedAtUtc",
                table: "ResponseCrmDeliveries");
        }
    }
}
