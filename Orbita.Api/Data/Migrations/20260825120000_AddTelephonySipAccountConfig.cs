using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260825120000_AddTelephonySipAccountConfig")]
public partial class AddTelephonySipAccountConfig : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "SipAccountProtected",
            table: "CrmTelephonyWebhooks",
            type: "character varying(8192)",
            maxLength: 8192,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "SipAccountProtected",
            table: "CrmTelephonyWebhooks");
    }
}
