using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Orbita.Api.Data.Migrations;

[DbContext(typeof(OrbitaDbContext))]
[Migration("20260908160000_TuneHighChurnTableAutovacuum")]
public sealed class TuneHighChurnTableAutovacuum : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            return;
        }

        migrationBuilder.Sql("""
            CREATE EXTENSION IF NOT EXISTS pg_stat_statements;

            ALTER TABLE "WorkerSnapshots" SET (
                autovacuum_vacuum_scale_factor = 0.02,
                autovacuum_analyze_scale_factor = 0.01,
                autovacuum_vacuum_threshold = 1000
            );
            ALTER TABLE "CandidateResponses" SET (
                autovacuum_vacuum_scale_factor = 0.02,
                autovacuum_analyze_scale_factor = 0.01,
                autovacuum_vacuum_threshold = 1000
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            return;
        }

        migrationBuilder.Sql("""
            ALTER TABLE "WorkerSnapshots" RESET (
                autovacuum_vacuum_scale_factor,
                autovacuum_analyze_scale_factor,
                autovacuum_vacuum_threshold
            );
            ALTER TABLE "CandidateResponses" RESET (
                autovacuum_vacuum_scale_factor,
                autovacuum_analyze_scale_factor,
                autovacuum_vacuum_threshold
            );
            """);
    }
}
