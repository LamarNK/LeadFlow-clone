using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAvitoListingPresentationFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AddressText",
                table: "WorkerAvitoAds",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "CanPublish",
                table: "WorkerAvitoAds",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "City",
                table: "WorkerAvitoAds",
                type: "character varying(300)",
                maxLength: 300,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Contacts",
                table: "WorkerAvitoAds",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DistrictText",
                table: "WorkerAvitoAds",
                type: "character varying(300)",
                maxLength: 300,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ErrorReason",
                table: "WorkerAvitoAds",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Favorites",
                table: "WorkerAvitoAds",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ImageUrl",
                table: "WorkerAvitoAds",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Salary",
                table: "WorkerAvitoAds",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SourceTab",
                table: "WorkerAvitoAds",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "active");

            migrationBuilder.AddColumn<int>(
                name: "Views",
                table: "WorkerAvitoAds",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AddressText",
                table: "WorkerAvitoAds");

            migrationBuilder.DropColumn(
                name: "CanPublish",
                table: "WorkerAvitoAds");

            migrationBuilder.DropColumn(
                name: "City",
                table: "WorkerAvitoAds");

            migrationBuilder.DropColumn(
                name: "Contacts",
                table: "WorkerAvitoAds");

            migrationBuilder.DropColumn(
                name: "DistrictText",
                table: "WorkerAvitoAds");

            migrationBuilder.DropColumn(
                name: "ErrorReason",
                table: "WorkerAvitoAds");

            migrationBuilder.DropColumn(
                name: "Favorites",
                table: "WorkerAvitoAds");

            migrationBuilder.DropColumn(
                name: "ImageUrl",
                table: "WorkerAvitoAds");

            migrationBuilder.DropColumn(
                name: "Salary",
                table: "WorkerAvitoAds");

            migrationBuilder.DropColumn(
                name: "SourceTab",
                table: "WorkerAvitoAds");

            migrationBuilder.DropColumn(
                name: "Views",
                table: "WorkerAvitoAds");
        }
    }
}
