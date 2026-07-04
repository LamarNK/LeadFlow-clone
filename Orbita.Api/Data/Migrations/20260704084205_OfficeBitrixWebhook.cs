using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class OfficeBitrixWebhook : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BitrixLastValidatedAtUtc",
                table: "Offices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BitrixPortalHost",
                table: "Offices",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BitrixUpdatedAtUtc",
                table: "Offices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BitrixUpdatedByUserId",
                table: "Offices",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BitrixValidationMessage",
                table: "Offices",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BitrixValidationStatus",
                table: "Offices",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "BitrixWebhookUrlProtected",
                table: "Offices",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BitrixLastValidatedAtUtc",
                table: "Offices");

            migrationBuilder.DropColumn(
                name: "BitrixPortalHost",
                table: "Offices");

            migrationBuilder.DropColumn(
                name: "BitrixUpdatedAtUtc",
                table: "Offices");

            migrationBuilder.DropColumn(
                name: "BitrixUpdatedByUserId",
                table: "Offices");

            migrationBuilder.DropColumn(
                name: "BitrixValidationMessage",
                table: "Offices");

            migrationBuilder.DropColumn(
                name: "BitrixValidationStatus",
                table: "Offices");

            migrationBuilder.DropColumn(
                name: "BitrixWebhookUrlProtected",
                table: "Offices");
        }
    }
}
