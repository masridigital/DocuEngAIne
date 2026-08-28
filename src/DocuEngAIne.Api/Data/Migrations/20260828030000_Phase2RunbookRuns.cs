using System;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocuEngAIne.Api.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(DocuEngAIneDbContext))]
    [Migration("20260828030000_Phase2RunbookRuns")]
    public class Phase2RunbookRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RunbookRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunbookId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    StartedByObjectId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunbookRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunbookRuns_Runbooks_RunbookId",
                        column: x => x.RunbookId,
                        principalTable: "Runbooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RunbookRuns_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RunbookRuns_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RunbookRuns_CompanyId",
                table: "RunbookRuns",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_RunbookRuns_RunbookId_StartedAt",
                table: "RunbookRuns",
                columns: new[] { "RunbookId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RunbookRuns_TenantId_StartedAt",
                table: "RunbookRuns",
                columns: new[] { "TenantId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "RunbookRuns");
        }
    }
}
