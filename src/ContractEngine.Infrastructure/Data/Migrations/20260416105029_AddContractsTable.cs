using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContractEngine.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddContractsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "contracts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    counterparty_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "varchar(500)", nullable: false),
                    reference_number = table.Column<string>(type: "varchar(100)", nullable: true),
                    contract_type = table.Column<string>(type: "varchar(50)", nullable: false),
                    status = table.Column<string>(type: "varchar(20)", nullable: false, defaultValue: "draft"),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: true),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    renewal_notice_days = table.Column<int>(type: "integer", nullable: false, defaultValue: 90),
                    auto_renewal = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    auto_renewal_period_months = table.Column<int>(type: "integer", nullable: true),
                    total_value = table.Column<decimal>(type: "numeric(15,2)", nullable: true),
                    currency = table.Column<string>(type: "varchar(3)", nullable: false, defaultValue: "USD"),
                    governing_law = table.Column<string>(type: "varchar(100)", nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: true),
                    rag_document_id = table.Column<string>(type: "varchar(255)", nullable: true),
                    current_version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contracts", x => x.id);
                    table.ForeignKey(
                        name: "FK_contracts_counterparties_counterparty_id",
                        column: x => x.counterparty_id,
                        principalTable: "counterparties",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_contracts_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_contracts_counterparty_id",
                table: "contracts",
                column: "counterparty_id");

            migrationBuilder.CreateIndex(
                name: "ix_contracts_tenant_id_counterparty_id",
                table: "contracts",
                columns: new[] { "tenant_id", "counterparty_id" });

            migrationBuilder.CreateIndex(
                name: "ix_contracts_tenant_id_end_date",
                table: "contracts",
                columns: new[] { "tenant_id", "end_date" });

            migrationBuilder.CreateIndex(
                name: "ix_contracts_tenant_id_reference_number",
                table: "contracts",
                columns: new[] { "tenant_id", "reference_number" });

            migrationBuilder.CreateIndex(
                name: "ix_contracts_tenant_id_status",
                table: "contracts",
                columns: new[] { "tenant_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contracts");
        }
    }
}
