using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260827120000_AddCrmSuccessDocuments")]
public partial class AddCrmSuccessDocuments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "SuccessContractMissingReason",
            table: "CrmCandidateCards",
            type: "character varying(2000)",
            maxLength: 2000,
            nullable: true);

        migrationBuilder.CreateTable(
            name: "CrmSuccessDocuments",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CardId = table.Column<Guid>(type: "uuid", nullable: false),
                Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                ContentType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                UploadedByUserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                UploadedByName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                RelativePath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CrmSuccessDocuments", x => x.Id);
                table.ForeignKey(
                    name: "FK_CrmSuccessDocuments_CrmCandidateCards_CardId",
                    column: x => x.CardId,
                    principalTable: "CrmCandidateCards",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_CrmSuccessDocuments_CardId_Category_CreatedAtUtc",
            table: "CrmSuccessDocuments",
            columns: new[] { "CardId", "Category", "CreatedAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "CrmSuccessDocuments");

        migrationBuilder.DropColumn(
            name: "SuccessContractMissingReason",
            table: "CrmCandidateCards");
    }
}
