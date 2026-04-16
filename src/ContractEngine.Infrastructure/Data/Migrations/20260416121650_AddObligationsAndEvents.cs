using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContractEngine.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddObligationsAndEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "obligations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    obligation_type = table.Column<string>(type: "varchar(50)", nullable: false),
                    status = table.Column<string>(type: "varchar(20)", nullable: false, defaultValue: "pending"),
                    title = table.Column<string>(type: "varchar(500)", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    responsible_party = table.Column<string>(type: "varchar(50)", nullable: false, defaultValue: "us"),
                    deadline_date = table.Column<DateOnly>(type: "date", nullable: true),
                    deadline_formula = table.Column<string>(type: "varchar(255)", nullable: true),
                    recurrence = table.Column<string>(type: "varchar(50)", nullable: true),
                    next_due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(15,2)", nullable: true),
                    currency = table.Column<string>(type: "varchar(3)", nullable: false, defaultValue: "USD"),
                    alert_window_days = table.Column<int>(type: "integer", nullable: false, defaultValue: 30),
                    grace_period_days = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    business_day_calendar = table.Column<string>(type: "varchar(50)", nullable: false, defaultValue: "US"),
                    source = table.Column<string>(type: "varchar(20)", nullable: false, defaultValue: "manual"),
                    extraction_job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    confidence_score = table.Column<decimal>(type: "numeric(3,2)", nullable: true),
                    clause_reference = table.Column<string>(type: "varchar(255)", nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_obligations", x => x.id);
                    table.ForeignKey(
                        name: "FK_obligations_contracts_contract_id",
                        column: x => x.contract_id,
                        principalTable: "contracts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_obligations_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "obligation_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    obligation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    from_status = table.Column<string>(type: "varchar(20)", nullable: false),
                    to_status = table.Column<string>(type: "varchar(20)", nullable: false),
                    actor = table.Column<string>(type: "varchar(255)", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_obligation_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_obligation_events_obligations_obligation_id",
                        column: x => x.obligation_id,
                        principalTable: "obligations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_obligation_events_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_obligation_events_obligation_id",
                table: "obligation_events",
                column: "obligation_id");

            migrationBuilder.CreateIndex(
                name: "ix_obligation_events_tenant_id_obligation_id_created_at",
                table: "obligation_events",
                columns: new[] { "tenant_id", "obligation_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_obligations_contract_id",
                table: "obligations",
                column: "contract_id");

            migrationBuilder.CreateIndex(
                name: "ix_obligations_tenant_id_contract_id",
                table: "obligations",
                columns: new[] { "tenant_id", "contract_id" });

            migrationBuilder.CreateIndex(
                name: "ix_obligations_tenant_id_next_due_date",
                table: "obligations",
                columns: new[] { "tenant_id", "next_due_date" });

            migrationBuilder.CreateIndex(
                name: "ix_obligations_tenant_id_obligation_type",
                table: "obligations",
                columns: new[] { "tenant_id", "obligation_type" });

            migrationBuilder.CreateIndex(
                name: "ix_obligations_tenant_id_status",
                table: "obligations",
                columns: new[] { "tenant_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "obligation_events");

            migrationBuilder.DropTable(
                name: "obligations");
        }
    }
}
