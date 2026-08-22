using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260821130000_AddTelephonyOutboundProvider")]
public partial class AddTelephonyOutboundProvider : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "OutboundProvider",
            table: "CrmTelephonyUserBindings",
            type: "character varying(32)",
            maxLength: 32,
            nullable: false,
            defaultValue: "default");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "OutboundProvider",
            table: "CrmTelephonyUserBindings");
    }
}
