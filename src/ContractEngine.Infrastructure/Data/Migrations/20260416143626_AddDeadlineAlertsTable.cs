using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContractEngine.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDeadlineAlertsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "deadline_alerts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    obligation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alert_type = table.Column<string>(type: "varchar(50)", nullable: false),
                    days_remaining = table.Column<int>(type: "integer", nullable: true),
                    message = table.Column<string>(type: "text", nullable: false),
                    acknowledged = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    acknowledged_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    acknowledged_by = table.Column<string>(type: "varchar(255)", nullable: true),
                    notification_sent = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    notification_sent_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deadline_alerts", x => x.id);
                    table.ForeignKey(
                        name: "FK_deadline_alerts_contracts_contract_id",
                        column: x => x.contract_id,
                        principalTable: "contracts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_deadline_alerts_obligations_obligation_id",
                        column: x => x.obligation_id,
                        principalTable: "obligations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_deadline_alerts_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_deadline_alerts_contract_id",
                table: "deadline_alerts",
                column: "contract_id");

            migrationBuilder.CreateIndex(
                name: "IX_deadline_alerts_obligation_id",
                table: "deadline_alerts",
                column: "obligation_id");

            migrationBuilder.CreateIndex(
                name: "ix_deadline_alerts_tenant_id_acknowledged_created_at",
                table: "deadline_alerts",
                columns: new[] { "tenant_id", "acknowledged", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_deadline_alerts_tenant_id_obligation_id",
                table: "deadline_alerts",
                columns: new[] { "tenant_id", "obligation_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "deadline_alerts");
        }
    }
}
