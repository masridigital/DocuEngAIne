using System;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocuEngAIne.Api.Data.Migrations
{
    /// <summary>
    /// Tenant lifecycle: <c>Tenants.Status</c> (Active = 0, Suspended = 1, Archived = 2) replaces the
    /// <c>IsActive</c> flag, with <c>StatusReason</c>, <c>StatusChangedAt</c> and
    /// <c>StatusChangedByObjectId</c>. A tenant that was inactive becomes Suspended with a reason saying
    /// so; every other tenant stays Active.
    /// </summary>
    [DbContext(typeof(DocuEngAIneDbContext))]
    [Migration("20260928200000_TenantStatusLifecycle")]
    public class TenantStatusLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "Tenants",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "StatusReason",
                table: "Tenants",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StatusChangedAt",
                table: "Tenants",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StatusChangedByObjectId",
                table: "Tenants",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE Tenants SET Status = 1, StatusReason = N'Deactivated before tenant statuses existed.', StatusChangedAt = SYSDATETIMEOFFSET() WHERE IsActive = 0;");

            migrationBuilder.DropColumn(name: "IsActive", table: "Tenants");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "Tenants",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.Sql("UPDATE Tenants SET IsActive = CASE WHEN Status = 0 THEN 1 ELSE 0 END;");

            migrationBuilder.DropColumn(name: "Status", table: "Tenants");
            migrationBuilder.DropColumn(name: "StatusReason", table: "Tenants");
            migrationBuilder.DropColumn(name: "StatusChangedAt", table: "Tenants");
            migrationBuilder.DropColumn(name: "StatusChangedByObjectId", table: "Tenants");
        }
    }
}
