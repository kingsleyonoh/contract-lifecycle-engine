using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContractEngine.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialTenantsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // gen_random_uuid() is built-in on Postgres 13+; the extension is a no-op on 16+
            // but defensive for any older image that slips into a dev environment.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pgcrypto;");

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    name = table.Column<string>(type: "varchar(255)", nullable: false),
                    api_key_hash = table.Column<string>(type: "varchar(512)", nullable: false),
                    api_key_prefix = table.Column<string>(type: "varchar(20)", nullable: false),
                    default_timezone = table.Column<string>(type: "varchar(50)", nullable: false, defaultValue: "UTC"),
                    default_currency = table.Column<string>(type: "varchar(3)", nullable: false, defaultValue: "USD"),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    metadata = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tenants_api_key_hash",
                table: "tenants",
                column: "api_key_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenants");
        }
    }
}
