using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocuEngAIne.Api.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(DocuEngAIneDbContext))]
    [Migration("20260827220000_Phase2IntegrationsCascadeFix")]
    public class Phase2IntegrationsCascadeFix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_IntegrationMappings_Tenants_TenantId",
                table: "IntegrationMappings");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuns_Tenants_TenantId",
                table: "SyncRuns");

            migrationBuilder.AddForeignKey(
                name: "FK_IntegrationMappings_Tenants_TenantId",
                table: "IntegrationMappings",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuns_Tenants_TenantId",
                table: "SyncRuns",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_IntegrationMappings_Tenants_TenantId",
                table: "IntegrationMappings");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncRuns_Tenants_TenantId",
                table: "SyncRuns");

            migrationBuilder.AddForeignKey(
                name: "FK_IntegrationMappings_Tenants_TenantId",
                table: "IntegrationMappings",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SyncRuns_Tenants_TenantId",
                table: "SyncRuns",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
