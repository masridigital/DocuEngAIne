using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocuEngAIne.Api.Data.Migrations
{
    /// <summary>
    /// Regional settings: <c>Tenants.TimeZoneId</c> (IANA), <c>DateFormat</c> and <c>TimeFormat</c>,
    /// all null, so every tenant stays on UTC, <c>yyyy-MM-dd</c> and a 24-hour clock until an
    /// administrator changes them.
    /// </summary>
    [DbContext(typeof(DocuEngAIneDbContext))]
    [Migration("20260928220000_TenantRegionalSettings")]
    public class TenantRegionalSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                table: "Tenants",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DateFormat",
                table: "Tenants",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeFormat",
                table: "Tenants",
                type: "nvarchar(8)",
                maxLength: 8,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "TimeZoneId", table: "Tenants");
            migrationBuilder.DropColumn(name: "DateFormat", table: "Tenants");
            migrationBuilder.DropColumn(name: "TimeFormat", table: "Tenants");
        }
    }
}
