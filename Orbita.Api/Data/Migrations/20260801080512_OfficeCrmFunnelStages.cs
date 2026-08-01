using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class OfficeCrmFunnelStages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CrmStagesJson",
                table: "Offices",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            // Operators are office-bound again: rebind any operators left without OfficeId
            // (e.g. after a prior experimental ownership migration).
            migrationBuilder.Sql(
                """
                WITH target AS (
                    SELECT "Id"
                    FROM "Offices"
                    WHERE "IsEnabled" = TRUE
                    ORDER BY "CreatedAtUtc"
                    LIMIT 1
                )
                UPDATE "PanelUserProfiles" p
                SET "OfficeId" = target."Id"
                FROM "AspNetUserRoles" ur
                INNER JOIN "AspNetRoles" r ON r."Id" = ur."RoleId"
                CROSS JOIN target
                WHERE p."UserId" = ur."UserId"
                  AND r."Name" = 'Operator'
                  AND p."OfficeId" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CrmStagesJson",
                table: "Offices");
        }
    }
}
