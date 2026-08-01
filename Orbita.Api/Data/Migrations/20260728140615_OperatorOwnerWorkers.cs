using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class OperatorOwnerWorkers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Optional owner marker on workers (not used for access control).
            migrationBuilder.AddColumn<string>(
                name: "OwnerUserId",
                table: "Workers",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Workers_OwnerUserId",
                table: "Workers",
                column: "OwnerUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Workers_OwnerUserId",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "Workers");
        }
    }
}
