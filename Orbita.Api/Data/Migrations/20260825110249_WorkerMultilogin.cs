using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations;

/// <inheritdoc />
public partial class WorkerMultilogin : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "MultiloginAutomationToken",
            table: "Workers",
            type: "character varying(2048)",
            maxLength: 2048,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "MultiloginCloudApiUrl",
            table: "Workers",
            type: "character varying(512)",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "MultiloginLauncherUrl",
            table: "Workers",
            type: "character varying(512)",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "MultiloginFolderId",
            table: "WorkerAccounts",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "MultiloginProfileId",
            table: "WorkerAccounts",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "MultiloginProfileName",
            table: "WorkerAccounts",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "MultiloginAutomationToken",
            table: "Workers");

        migrationBuilder.DropColumn(
            name: "MultiloginCloudApiUrl",
            table: "Workers");

        migrationBuilder.DropColumn(
            name: "MultiloginLauncherUrl",
            table: "Workers");

        migrationBuilder.DropColumn(
            name: "MultiloginFolderId",
            table: "WorkerAccounts");

        migrationBuilder.DropColumn(
            name: "MultiloginProfileId",
            table: "WorkerAccounts");

        migrationBuilder.DropColumn(
            name: "MultiloginProfileName",
            table: "WorkerAccounts");
    }
}
