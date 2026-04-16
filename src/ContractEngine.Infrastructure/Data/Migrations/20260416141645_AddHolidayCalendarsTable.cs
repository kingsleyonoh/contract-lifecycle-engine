using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContractEngine.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHolidayCalendarsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "holiday_calendars",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    calendar_code = table.Column<string>(type: "varchar(50)", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    holiday_date = table.Column<DateOnly>(type: "date", nullable: false),
                    holiday_name = table.Column<string>(type: "varchar(255)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holiday_calendars", x => x.id);
                    table.ForeignKey(
                        name: "FK_holiday_calendars_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_holiday_calendars_calendar_code_year_holiday_date",
                table: "holiday_calendars",
                columns: new[] { "calendar_code", "year", "holiday_date" });

            migrationBuilder.CreateIndex(
                name: "ix_holiday_calendars_tenant_id_calendar_code",
                table: "holiday_calendars",
                columns: new[] { "tenant_id", "calendar_code" });

            // Raw SQL — Postgres treats NULLs as distinct by default, which would let two
            // system-wide rows (tenant_id IS NULL) with the same (calendar_code, holiday_date)
            // slip through. NULLS NOT DISTINCT (Postgres 15+) makes the UNIQUE constraint treat
            // NULLs as equal, enforcing "one system-wide row per (code, date)".
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX ux_holiday_calendars_tenant_id_calendar_code_holiday_date
                ON holiday_calendars (tenant_id, calendar_code, holiday_date)
                NULLS NOT DISTINCT;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "holiday_calendars");
        }
    }
}
