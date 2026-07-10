using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260709120000_BitrixDistribution")]
public partial class BitrixDistribution : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "BitrixInstances",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OfficeId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Signature = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                WebhookUrlProtected = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                PortalHost = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                ValidationStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ValidationMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                LastValidatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                IntegrationSettingsJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedByUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BitrixInstances", x => x.Id);
                table.ForeignKey(
                    name: "FK_BitrixInstances_Offices_OfficeId",
                    column: x => x.OfficeId,
                    principalTable: "Offices",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "DistributionRoutes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OfficeId = table.Column<Guid>(type: "uuid", nullable: false),
                IsAutoDistributionEnabled = table.Column<bool>(type: "boolean", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedByUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DistributionRoutes", x => x.Id);
                table.ForeignKey(
                    name: "FK_DistributionRoutes_Offices_OfficeId",
                    column: x => x.OfficeId,
                    principalTable: "Offices",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "DistributionNodes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                RouteId = table.Column<Guid>(type: "uuid", nullable: false),
                ParentNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                BitrixInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                SortOrder = table.Column<int>(type: "integer", nullable: false),
                EditorPositionX = table.Column<double>(type: "double precision", nullable: false),
                EditorPositionY = table.Column<double>(type: "double precision", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DistributionNodes", x => x.Id);
                table.ForeignKey(
                    name: "FK_DistributionNodes_BitrixInstances_BitrixInstanceId",
                    column: x => x.BitrixInstanceId,
                    principalTable: "BitrixInstances",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_DistributionNodes_DistributionNodes_ParentNodeId",
                    column: x => x.ParentNodeId,
                    principalTable: "DistributionNodes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_DistributionNodes_DistributionRoutes_RouteId",
                    column: x => x.RouteId,
                    principalTable: "DistributionRoutes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "DistributionRoundRobinStates",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                RouteId = table.Column<Guid>(type: "uuid", nullable: false),
                ParentNodeId = table.Column<Guid>(type: "uuid", nullable: true),
                NextChildIndex = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DistributionRoundRobinStates", x => x.Id);
                table.ForeignKey(
                    name: "FK_DistributionRoundRobinStates_DistributionRoutes_RouteId",
                    column: x => x.RouteId,
                    principalTable: "DistributionRoutes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.AddColumn<Guid>(
            name: "BitrixInstanceId",
            table: "CandidateResponses",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "DuplicateBitrixInstanceId",
            table: "CandidateResponses",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "DistributionMode",
            table: "CandidateResponses",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "");

        migrationBuilder.CreateIndex(
            name: "IX_BitrixInstances_OfficeId",
            table: "BitrixInstances",
            column: "OfficeId");

        migrationBuilder.CreateIndex(
            name: "IX_CandidateResponses_BitrixInstanceId",
            table: "CandidateResponses",
            column: "BitrixInstanceId");

        migrationBuilder.CreateIndex(
            name: "IX_CandidateResponses_DuplicateBitrixInstanceId",
            table: "CandidateResponses",
            column: "DuplicateBitrixInstanceId");

        migrationBuilder.CreateIndex(
            name: "IX_DistributionNodes_BitrixInstanceId",
            table: "DistributionNodes",
            column: "BitrixInstanceId");

        migrationBuilder.CreateIndex(
            name: "IX_DistributionNodes_ParentNodeId",
            table: "DistributionNodes",
            column: "ParentNodeId");

        migrationBuilder.CreateIndex(
            name: "IX_DistributionNodes_RouteId",
            table: "DistributionNodes",
            column: "RouteId");

        migrationBuilder.CreateIndex(
            name: "IX_DistributionNodes_RouteId_ParentNodeId_SortOrder",
            table: "DistributionNodes",
            columns: new[] { "RouteId", "ParentNodeId", "SortOrder" });

        migrationBuilder.CreateIndex(
            name: "IX_DistributionRoutes_OfficeId",
            table: "DistributionRoutes",
            column: "OfficeId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_DistributionRoundRobinStates_RouteId_ParentNodeId",
            table: "DistributionRoundRobinStates",
            columns: new[] { "RouteId", "ParentNodeId" },
            unique: true);

        migrationBuilder.AddForeignKey(
            name: "FK_CandidateResponses_BitrixInstances_BitrixInstanceId",
            table: "CandidateResponses",
            column: "BitrixInstanceId",
            principalTable: "BitrixInstances",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);

        migrationBuilder.AddForeignKey(
            name: "FK_CandidateResponses_BitrixInstances_DuplicateBitrixInstanceId",
            table: "CandidateResponses",
            column: "DuplicateBitrixInstanceId",
            principalTable: "BitrixInstances",
            principalColumn: "Id",
            onDelete: ReferentialAction.SetNull);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_CandidateResponses_BitrixInstances_BitrixInstanceId",
            table: "CandidateResponses");

        migrationBuilder.DropForeignKey(
            name: "FK_CandidateResponses_BitrixInstances_DuplicateBitrixInstanceId",
            table: "CandidateResponses");

        migrationBuilder.DropTable(name: "DistributionNodes");
        migrationBuilder.DropTable(name: "DistributionRoundRobinStates");
        migrationBuilder.DropTable(name: "DistributionRoutes");
        migrationBuilder.DropTable(name: "BitrixInstances");

        migrationBuilder.DropIndex(name: "IX_CandidateResponses_BitrixInstanceId", table: "CandidateResponses");
        migrationBuilder.DropIndex(name: "IX_CandidateResponses_DuplicateBitrixInstanceId", table: "CandidateResponses");

        migrationBuilder.DropColumn(name: "BitrixInstanceId", table: "CandidateResponses");
        migrationBuilder.DropColumn(name: "DuplicateBitrixInstanceId", table: "CandidateResponses");
        migrationBuilder.DropColumn(name: "DistributionMode", table: "CandidateResponses");
    }
}