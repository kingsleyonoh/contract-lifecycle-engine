using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContractEngine.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExtractionPromptsAndJobsTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "extraction_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    contract_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "varchar(20)", nullable: false, defaultValue: "queued"),
                    prompt_types = table.Column<string[]>(type: "text[]", nullable: false),
                    obligations_found = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    obligations_confirmed = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    rag_document_id = table.Column<string>(type: "varchar(255)", nullable: true),
                    raw_responses = table.Column<string>(type: "jsonb", nullable: true),
                    started_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    retry_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_extraction_jobs", x => x.id);
                    table.ForeignKey(
                        name: "FK_extraction_jobs_contract_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "contract_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_extraction_jobs_contracts_contract_id",
                        column: x => x.contract_id,
                        principalTable: "contracts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_extraction_jobs_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "extraction_prompts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    prompt_type = table.Column<string>(type: "varchar(50)", nullable: false),
                    prompt_text = table.Column<string>(type: "text", nullable: false),
                    response_schema = table.Column<string>(type: "jsonb", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_extraction_prompts", x => x.id);
                    table.ForeignKey(
                        name: "FK_extraction_prompts_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_extraction_jobs_contract_id",
                table: "extraction_jobs",
                column: "contract_id");

            migrationBuilder.CreateIndex(
                name: "IX_extraction_jobs_document_id",
                table: "extraction_jobs",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ix_extraction_jobs_tenant_id_contract_id",
                table: "extraction_jobs",
                columns: new[] { "tenant_id", "contract_id" });

            migrationBuilder.CreateIndex(
                name: "ix_extraction_jobs_tenant_id_status",
                table: "extraction_jobs",
                columns: new[] { "tenant_id", "status" });

            // Raw SQL — EF Core doesn't expose NULLS NOT DISTINCT (Postgres 15+). Without it,
            // two system-default rows (tenant_id IS NULL) with the same prompt_type would be allowed.
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX ux_extraction_prompts_tenant_id_prompt_type
                ON extraction_prompts (tenant_id, prompt_type)
                NULLS NOT DISTINCT;
            ");

            // Deferred FK: obligations.extraction_job_id → extraction_jobs(id) ON DELETE SET NULL.
            // The column was created in Batch 011 as a nullable uuid with no FK. Now that the
            // extraction_jobs table exists we can add the real constraint. First, NULL out any
            // orphaned GUIDs that were inserted by tests or seed data before this table existed.
            migrationBuilder.Sql(@"
                UPDATE obligations
                SET extraction_job_id = NULL
                WHERE extraction_job_id IS NOT NULL;
            ");

            migrationBuilder.AddForeignKey(
                name: "FK_obligations_extraction_jobs_extraction_job_id",
                table: "obligations",
                column: "extraction_job_id",
                principalTable: "extraction_jobs",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop the deferred FK first (before dropping the principal table).
            migrationBuilder.DropForeignKey(
                name: "FK_obligations_extraction_jobs_extraction_job_id",
                table: "obligations");

            migrationBuilder.DropTable(
                name: "extraction_jobs");

            migrationBuilder.DropTable(
                name: "extraction_prompts");
        }
    }
}
