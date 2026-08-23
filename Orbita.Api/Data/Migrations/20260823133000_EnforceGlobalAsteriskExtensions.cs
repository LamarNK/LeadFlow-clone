using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260823133000_EnforceGlobalAsteriskExtensions")]
public partial class EnforceGlobalAsteriskExtensions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_CrmTelephonyUserBindings_Asterisk_ProviderUserKey",
            table: "CrmTelephonyUserBindings",
            columns: new[] { "Provider", "ProviderUserKey" },
            unique: true,
            filter: "\"Provider\" = 'asterisk'");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_CrmTelephonyUserBindings_Asterisk_ProviderUserKey",
            table: "CrmTelephonyUserBindings");
    }
}
