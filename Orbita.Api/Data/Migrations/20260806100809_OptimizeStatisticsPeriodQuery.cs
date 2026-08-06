using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class OptimizeStatisticsPeriodQuery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CandidateResponses_WorkerId",
                table: "CandidateResponses");

            migrationBuilder.CreateIndex(
                name: "IX_CandidateResponses_WorkerId_CollectedAt",
                table: "CandidateResponses",
                columns: new[] { "WorkerId", "CollectedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CandidateResponses_WorkerId_CollectedAt",
                table: "CandidateResponses");

            migrationBuilder.CreateIndex(
                name: "IX_CandidateResponses_WorkerId",
                table: "CandidateResponses",
                column: "WorkerId");
        }
    }
}
