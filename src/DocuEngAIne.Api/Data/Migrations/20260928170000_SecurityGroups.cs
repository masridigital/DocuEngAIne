using System;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocuEngAIne.Api.Data.Migrations
{
    /// <summary>
    /// Security groups for company scoping: <c>SecurityGroups</c>, <c>SecurityGroupMembers</c> and
    /// <c>SecurityGroupCompanyGrants</c>. Members cascade with their group and user, grants with their
    /// group and company; every Tenant FK is Restrict (the cascade already arrives via Users /
    /// Companies). Adds <c>ArchiveEntries.CompanyId</c>, backfilled from the archived rows, so the
    /// Museum can be scoped without joining rows the soft-delete filter hides.
    /// </summary>
    [DbContext(typeof(DocuEngAIneDbContext))]
    [Migration("20260928170000_SecurityGroups")]
    public class SecurityGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CompanyId",
                table: "ArchiveEntries",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE e SET e.CompanyId = r.CompanyId FROM ArchiveEntries e JOIN Assets r ON r.Id = e.ResourceId WHERE e.ResourceType = N'Asset';
                UPDATE e SET e.CompanyId = r.CompanyId FROM ArchiveEntries e JOIN Documents r ON r.Id = e.ResourceId WHERE e.ResourceType = N'Document';
                UPDATE e SET e.CompanyId = r.CompanyId FROM ArchiveEntries e JOIN Runbooks r ON r.Id = e.ResourceId WHERE e.ResourceType = N'Runbook';
                UPDATE e SET e.CompanyId = r.CompanyId FROM ArchiveEntries e JOIN KeeperLinks r ON r.Id = e.ResourceId WHERE e.ResourceType = N'KeeperLink';
                """);

            migrationBuilder.CreateTable(
                name: "SecurityGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IncludeTenantWide = table.Column<bool>(type: "bit", nullable: false),
                    CreatedByObjectId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SecurityGroups_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SecurityGroupMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SecurityGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityGroupMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SecurityGroupMembers_SecurityGroups_SecurityGroupId",
                        column: x => x.SecurityGroupId,
                        principalTable: "SecurityGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SecurityGroupMembers_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SecurityGroupMembers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SecurityGroupCompanyGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SecurityGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Level = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityGroupCompanyGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SecurityGroupCompanyGrants_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SecurityGroupCompanyGrants_SecurityGroups_SecurityGroupId",
                        column: x => x.SecurityGroupId,
                        principalTable: "SecurityGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SecurityGroupCompanyGrants_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityGroups_TenantId_Name",
                table: "SecurityGroups",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurityGroupMembers_SecurityGroupId_UserId",
                table: "SecurityGroupMembers",
                columns: new[] { "SecurityGroupId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurityGroupMembers_TenantId_UserId",
                table: "SecurityGroupMembers",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityGroupMembers_UserId",
                table: "SecurityGroupMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityGroupCompanyGrants_CompanyId",
                table: "SecurityGroupCompanyGrants",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityGroupCompanyGrants_SecurityGroupId_CompanyId",
                table: "SecurityGroupCompanyGrants",
                columns: new[] { "SecurityGroupId", "CompanyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurityGroupCompanyGrants_TenantId_CompanyId",
                table: "SecurityGroupCompanyGrants",
                columns: new[] { "TenantId", "CompanyId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "SecurityGroupCompanyGrants");
            migrationBuilder.DropTable(name: "SecurityGroupMembers");
            migrationBuilder.DropTable(name: "SecurityGroups");

            migrationBuilder.DropColumn(name: "CompanyId", table: "ArchiveEntries");
        }
    }
}
