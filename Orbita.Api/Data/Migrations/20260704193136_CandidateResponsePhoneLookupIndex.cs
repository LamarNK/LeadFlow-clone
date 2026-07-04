using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CandidateResponsePhoneLookupIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_CandidateResponses_OfficeId_AccountId_AvitoSubProfileId_Pho~",
                table: "CandidateResponses",
                columns: new[] { "OfficeId", "AccountId", "AvitoSubProfileId", "PhoneNormalized" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CandidateResponses_OfficeId_AccountId_AvitoSubProfileId_Pho~",
                table: "CandidateResponses");
        }
    }
}
