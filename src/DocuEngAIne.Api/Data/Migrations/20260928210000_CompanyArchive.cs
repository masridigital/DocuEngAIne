using System;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocuEngAIne.Api.Data.Migrations
{
    /// <summary>
    /// Company archive: soft delete (<c>DeletedAt</c>) on Companies, with the unique slug index
    /// re-filtered to live rows like Documents and Runbooks, and <c>ArchiveEntries.ParentEntryId</c>,
    /// which files an item archived with its company under the company's entry.
    /// </summary>
    [DbContext(typeof(DocuEngAIneDbContext))]
    [Migration("20260928210000_CompanyArchive")]
    public class CompanyArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "Companies",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.DropIndex(
                name: "IX_Companies_TenantId_Slug",
                table: "Companies");

            migrationBuilder.CreateIndex(
                name: "IX_Companies_TenantId_Slug",
                table: "Companies",
                columns: new[] { "TenantId", "Slug" },
                unique: true,
                filter: "[Slug] IS NOT NULL AND [DeletedAt] IS NULL");

            migrationBuilder.AddColumn<Guid>(
                name: "ParentEntryId",
                table: "ArchiveEntries",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveEntries_ParentEntryId",
                table: "ArchiveEntries",
                column: "ParentEntryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ArchiveEntries_ParentEntryId",
                table: "ArchiveEntries");

            migrationBuilder.DropColumn(name: "ParentEntryId", table: "ArchiveEntries");

            migrationBuilder.DropIndex(
                name: "IX_Companies_TenantId_Slug",
                table: "Companies");

            migrationBuilder.CreateIndex(
                name: "IX_Companies_TenantId_Slug",
                table: "Companies",
                columns: new[] { "TenantId", "Slug" },
                unique: true);

            migrationBuilder.DropColumn(name: "DeletedAt", table: "Companies");
        }
    }
}
