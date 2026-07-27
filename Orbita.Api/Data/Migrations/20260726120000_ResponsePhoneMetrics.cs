using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260726120000_ResponsePhoneMetrics")]
public partial class ResponsePhoneMetrics : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "PhoneUnchangedHours",
            table: "Workers",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PhoneMetricKind",
            table: "CandidateResponses",
            type: "character varying(32)",
            maxLength: 32,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "PreviousPhoneRaw",
            table: "CandidateResponses",
            type: "character varying(64)",
            maxLength: 64,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "PreviousPhoneNormalized",
            table: "CandidateResponses",
            type: "character varying(32)",
            maxLength: 32,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<int>(
            name: "PhoneUnchangedHours",
            table: "CandidateResponses",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "PhoneChangedAtUtc",
            table: "CandidateResponses",
            type: "timestamp with time zone",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "PhoneUnchangedHours", table: "Workers");
        migrationBuilder.DropColumn(name: "PhoneMetricKind", table: "CandidateResponses");
        migrationBuilder.DropColumn(name: "PreviousPhoneRaw", table: "CandidateResponses");
        migrationBuilder.DropColumn(name: "PreviousPhoneNormalized", table: "CandidateResponses");
        migrationBuilder.DropColumn(name: "PhoneUnchangedHours", table: "CandidateResponses");
        migrationBuilder.DropColumn(name: "PhoneChangedAtUtc", table: "CandidateResponses");
    }
}
