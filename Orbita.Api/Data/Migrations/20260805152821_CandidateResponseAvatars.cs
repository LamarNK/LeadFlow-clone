using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CandidateResponseAvatars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AvatarContentType",
                table: "CandidateResponses",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "AvatarImage",
                table: "CandidateResponses",
                type: "bytea",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AvatarContentType",
                table: "CandidateResponses");

            migrationBuilder.DropColumn(
                name: "AvatarImage",
                table: "CandidateResponses");
        }
    }
}
