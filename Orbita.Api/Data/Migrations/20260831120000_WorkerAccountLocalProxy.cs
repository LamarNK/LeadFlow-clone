using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260831120000_WorkerAccountLocalProxy")]
public partial class WorkerAccountLocalProxy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "LocalProxyEnabled",
            table: "WorkerAccounts",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "LocalProxyAddress",
            table: "WorkerAccounts",
            type: "character varying(255)",
            maxLength: 255,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "LocalProxyUsername",
            table: "WorkerAccounts",
            type: "character varying(255)",
            maxLength: 255,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "LocalProxyPasswordProtected",
            table: "WorkerAccounts",
            type: "character varying(2048)",
            maxLength: 2048,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "LocalProxyEnabled", table: "WorkerAccounts");
        migrationBuilder.DropColumn(name: "LocalProxyAddress", table: "WorkerAccounts");
        migrationBuilder.DropColumn(name: "LocalProxyUsername", table: "WorkerAccounts");
        migrationBuilder.DropColumn(name: "LocalProxyPasswordProtected", table: "WorkerAccounts");
    }
}
