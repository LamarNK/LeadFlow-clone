using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class OfficeBitrixTransmissionEnabled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "BitrixTransmissionEnabled",
                table: "Offices",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BitrixTransmissionEnabled",
                table: "Offices");
        }
    }
}
