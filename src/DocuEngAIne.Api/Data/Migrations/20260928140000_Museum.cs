using System;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocuEngAIne.Api.Data.Migrations
{
    /// <summary>
    /// The Museum: soft delete (<c>DeletedAt</c>) on Assets, Documents, Runbooks and KeeperLinks, the
    /// <c>ArchiveEntries</c> registry, and the Documents / Runbooks unique slug indexes re-filtered to
    /// live rows only, so an archived row no longer holds its slug.
    /// </summary>
    [DbContext(typeof(DocuEngAIneDbContext))]
    [Migration("20260928140000_Museum")]
    public class Museum : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "Assets",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "Documents",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "Runbooks",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                table: "KeeperLinks",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.DropIndex(
                name: "IX_Documents_TenantId_Slug",
                table: "Documents");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_TenantId_Slug",
                table: "Documents",
                columns: new[] { "TenantId", "Slug" },
                unique: true,
                filter: "[Slug] IS NOT NULL AND [DeletedAt] IS NULL");

            migrationBuilder.DropIndex(
                name: "IX_Runbooks_TenantId_Slug",
                table: "Runbooks");

            migrationBuilder.CreateIndex(
                name: "IX_Runbooks_TenantId_Slug",
                table: "Runbooks",
                columns: new[] { "TenantId", "Slug" },
                unique: true,
                filter: "[Slug] IS NOT NULL AND [DeletedAt] IS NULL");

            migrationBuilder.CreateTable(
                name: "ArchiveEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourceType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ResourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourceLabel = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ArchivedByObjectId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ArchivedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RestoredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RestoredByObjectId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    PermanentlyDeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PermanentlyDeletedByObjectId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchiveEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArchiveEntries_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveEntries_TenantId_ArchivedAt",
                table: "ArchiveEntries",
                columns: new[] { "TenantId", "ArchivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ArchiveEntries_TenantId_ResourceType_ResourceId",
                table: "ArchiveEntries",
                columns: new[] { "TenantId", "ResourceType", "ResourceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ArchiveEntries");

            migrationBuilder.DropIndex(
                name: "IX_Runbooks_TenantId_Slug",
                table: "Runbooks");

            migrationBuilder.CreateIndex(
                name: "IX_Runbooks_TenantId_Slug",
                table: "Runbooks",
                columns: new[] { "TenantId", "Slug" },
                unique: true,
                filter: "[Slug] IS NOT NULL");

            migrationBuilder.DropIndex(
                name: "IX_Documents_TenantId_Slug",
                table: "Documents");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_TenantId_Slug",
                table: "Documents",
                columns: new[] { "TenantId", "Slug" },
                unique: true,
                filter: "[Slug] IS NOT NULL");

            migrationBuilder.DropColumn(name: "DeletedAt", table: "KeeperLinks");
            migrationBuilder.DropColumn(name: "DeletedAt", table: "Runbooks");
            migrationBuilder.DropColumn(name: "DeletedAt", table: "Documents");
            migrationBuilder.DropColumn(name: "DeletedAt", table: "Assets");
        }
    }
}
