using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260725130000_WorkerMessengerAutoReply")]
public partial class WorkerMessengerAutoReply : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "MessengerAutoReplyEnabled",
            table: "Workers",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "MessengerAutoReplyMessage",
            table: "Workers",
            type: "character varying(2000)",
            maxLength: 2000,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "MessengerAutoReplyEnabled", table: "Workers");
        migrationBuilder.DropColumn(name: "MessengerAutoReplyMessage", table: "Workers");
    }
}
