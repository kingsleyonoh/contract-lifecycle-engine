using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContractEngine.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddContractTagsAndVersionsTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "contract_tags",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tag = table.Column<string>(type: "varchar(100)", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contract_tags", x => x.id);
                    table.ForeignKey(
                        name: "FK_contract_tags_contracts_contract_id",
                        column: x => x.contract_id,
                        principalTable: "contracts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_contract_tags_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "contract_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    change_summary = table.Column<string>(type: "text", nullable: true),
                    diff_result = table.Column<string>(type: "jsonb", nullable: true),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: true),
                    created_by = table.Column<string>(type: "varchar(255)", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_contract_versions", x => x.id);
                    table.ForeignKey(
                        name: "FK_contract_versions_contracts_contract_id",
                        column: x => x.contract_id,
                        principalTable: "contracts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_contract_versions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_contract_tags_contract_id",
                table: "contract_tags",
                column: "contract_id");

            migrationBuilder.CreateIndex(
                name: "ix_contract_tags_tenant_id_tag",
                table: "contract_tags",
                columns: new[] { "tenant_id", "tag" });

            migrationBuilder.CreateIndex(
                name: "ux_contract_tags_tenant_id_contract_id_tag",
                table: "contract_tags",
                columns: new[] { "tenant_id", "contract_id", "tag" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_contract_versions_tenant_id_contract_id_version_number",
                table: "contract_versions",
                columns: new[] { "tenant_id", "contract_id", "version_number" });

            migrationBuilder.CreateIndex(
                name: "ux_contract_versions_contract_id_version_number",
                table: "contract_versions",
                columns: new[] { "contract_id", "version_number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "contract_tags");

            migrationBuilder.DropTable(
                name: "contract_versions");
        }
    }
}
