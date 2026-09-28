using System;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocuEngAIne.Api.Data.Migrations
{
    /// <summary>
    /// Asset layouts: publish state, company availability and version history on <c>AssetTypes</c>
    /// (existing layouts stay published and available to every company); section, help text and
    /// option list on <c>FieldDefinitions</c>; <c>OptionLists</c> / <c>OptionListItems</c>;
    /// <c>AssetTypeCompanyActivations</c> and <c>AssetTypeVersions</c>. Every Tenant FK is Restrict
    /// (the cascade already arrives through <c>AssetTypes</c>); a field's option list is Restrict too,
    /// so a list in use cannot be deleted.
    /// </summary>
    [DbContext(typeof(DocuEngAIneDbContext))]
    [Migration("20260928180000_AssetLayouts")]
    public class AssetLayouts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPublished",
                table: "AssetTypes",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PublishedAt",
                table: "AssetTypes",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AvailableToAllCompanies",
                table: "AssetTypes",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "CurrentVersion",
                table: "AssetTypes",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("UPDATE AssetTypes SET PublishedAt = CreatedAt;");

            migrationBuilder.CreateTable(
                name: "OptionLists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OptionLists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OptionLists_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OptionListItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OptionListId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Label = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OptionListItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OptionListItems_OptionLists_OptionListId",
                        column: x => x.OptionListId,
                        principalTable: "OptionLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddColumn<string>(
                name: "HelpText",
                table: "FieldDefinitions",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OptionListId",
                table: "FieldDefinitions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Section",
                table: "FieldDefinitions",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssetTypeCompanyActivations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssetTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActivatedByObjectId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetTypeCompanyActivations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssetTypeCompanyActivations_AssetTypes_AssetTypeId",
                        column: x => x.AssetTypeId,
                        principalTable: "AssetTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssetTypeCompanyActivations_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetTypeCompanyActivations_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssetTypeVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AssetTypeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    SchemaJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedByObjectId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CreatedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetTypeVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssetTypeVersions_AssetTypes_AssetTypeId",
                        column: x => x.AssetTypeId,
                        principalTable: "AssetTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssetTypeVersions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FieldDefinitions_OptionListId",
                table: "FieldDefinitions",
                column: "OptionListId");

            migrationBuilder.AddForeignKey(
                name: "FK_FieldDefinitions_OptionLists_OptionListId",
                table: "FieldDefinitions",
                column: "OptionListId",
                principalTable: "OptionLists",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.CreateIndex(
                name: "IX_OptionLists_TenantId_Name",
                table: "OptionLists",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OptionListItems_OptionListId_Value",
                table: "OptionListItems",
                columns: new[] { "OptionListId", "Value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssetTypeCompanyActivations_AssetTypeId_CompanyId",
                table: "AssetTypeCompanyActivations",
                columns: new[] { "AssetTypeId", "CompanyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssetTypeCompanyActivations_CompanyId",
                table: "AssetTypeCompanyActivations",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetTypeCompanyActivations_TenantId_CompanyId",
                table: "AssetTypeCompanyActivations",
                columns: new[] { "TenantId", "CompanyId" });

            migrationBuilder.CreateIndex(
                name: "IX_AssetTypeVersions_AssetTypeId_VersionNumber",
                table: "AssetTypeVersions",
                columns: new[] { "AssetTypeId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssetTypeVersions_TenantId",
                table: "AssetTypeVersions",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: "FK_FieldDefinitions_OptionLists_OptionListId", table: "FieldDefinitions");

            migrationBuilder.DropTable(name: "AssetTypeCompanyActivations");
            migrationBuilder.DropTable(name: "AssetTypeVersions");
            migrationBuilder.DropTable(name: "OptionListItems");
            migrationBuilder.DropTable(name: "OptionLists");

            migrationBuilder.DropIndex(name: "IX_FieldDefinitions_OptionListId", table: "FieldDefinitions");

            migrationBuilder.DropColumn(name: "HelpText", table: "FieldDefinitions");
            migrationBuilder.DropColumn(name: "OptionListId", table: "FieldDefinitions");
            migrationBuilder.DropColumn(name: "Section", table: "FieldDefinitions");

            migrationBuilder.DropColumn(name: "IsPublished", table: "AssetTypes");
            migrationBuilder.DropColumn(name: "PublishedAt", table: "AssetTypes");
            migrationBuilder.DropColumn(name: "AvailableToAllCompanies", table: "AssetTypes");
            migrationBuilder.DropColumn(name: "CurrentVersion", table: "AssetTypes");
        }
    }
}
