using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260723120000_WorkerAccountAvitoCredentials")]
public partial class WorkerAccountAvitoCredentials : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AvitoLogin",
            table: "WorkerAccounts",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AvitoPasswordProtected",
            table: "WorkerAccounts",
            type: "character varying(2048)",
            maxLength: 2048,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "AvitoLogin",
            table: "WorkerAccounts");

        migrationBuilder.DropColumn(
            name: "AvitoPasswordProtected",
            table: "WorkerAccounts");
    }
}
