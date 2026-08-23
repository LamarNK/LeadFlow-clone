using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260823120000_AddTelephonyWebRtcCredentials")]
public partial class AddTelephonyWebRtcCredentials : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "WebRtcAuthorizationUsername",
            table: "CrmTelephonyUserBindings",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "WebRtcPasswordProtected",
            table: "CrmTelephonyUserBindings",
            type: "character varying(8192)",
            maxLength: 8192,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "WebRtcAuthorizationUsername",
            table: "CrmTelephonyUserBindings");

        migrationBuilder.DropColumn(
            name: "WebRtcPasswordProtected",
            table: "CrmTelephonyUserBindings");
    }
}
