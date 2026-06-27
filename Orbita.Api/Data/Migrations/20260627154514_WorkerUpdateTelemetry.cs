using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkerUpdateTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastUpdateAtUtc",
                table: "Workers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastUpdateMessage",
                table: "Workers",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LastUpdateSuccess",
                table: "Workers",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastUpdateVersion",
                table: "Workers",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastUpdateAtUtc",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "LastUpdateMessage",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "LastUpdateSuccess",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "LastUpdateVersion",
                table: "Workers");
        }
    }
}
