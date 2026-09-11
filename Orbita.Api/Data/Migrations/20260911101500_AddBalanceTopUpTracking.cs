using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Orbita.Api.Data;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260911101500_AddBalanceTopUpTracking")]
public partial class AddBalanceTopUpTracking : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "AwaitingBalanceAtUtc",
            table: "TopUpSessions",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "BalanceAfter",
            table: "TopUpSessions",
            type: "numeric(18,2)",
            precision: 18,
            scale: 2,
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "BalanceConfirmedAtUtc",
            table: "TopUpSessions",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.DropIndex(
            name: "IX_TopUpSessions_Account_Active",
            table: "TopUpSessions");

        migrationBuilder.CreateIndex(
            name: "IX_TopUpSessions_Account_Active",
            table: "TopUpSessions",
            columns: new[] { "AccountId", "SubProfileId" },
            unique: true,
            filter: "\"Status\" IN ('requested', 'started', 'payment_claimed', 'qr_ready', 'awaiting_balance')");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_TopUpSessions_Account_Active",
            table: "TopUpSessions");

        migrationBuilder.DropColumn(name: "AwaitingBalanceAtUtc", table: "TopUpSessions");
        migrationBuilder.DropColumn(name: "BalanceAfter", table: "TopUpSessions");
        migrationBuilder.DropColumn(name: "BalanceConfirmedAtUtc", table: "TopUpSessions");

        migrationBuilder.CreateIndex(
            name: "IX_TopUpSessions_Account_Active",
            table: "TopUpSessions",
            column: "AccountId",
            unique: true,
            filter: "\"Status\" IN ('requested', 'started', 'payment_claimed', 'qr_ready')");
    }
}
