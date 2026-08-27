using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocuEngAIne.Api.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(DocuEngAIneDbContext))]
    [Migration("20260827223000_Phase2SyncPolicy")]
    public class Phase2SyncPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SkipInactive",
                table: "IntegrationConnections",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "SkipContacts",
                table: "IntegrationConnections",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "SkipLocations",
                table: "IntegrationConnections",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SkipAssets",
                table: "IntegrationConnections",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AutoUpdateAssetNames",
                table: "IntegrationConnections",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "UpdateCompanyDetails",
                table: "IntegrationConnections",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SkipInactive",
                table: "IntegrationConnections");

            migrationBuilder.DropColumn(
                name: "SkipContacts",
                table: "IntegrationConnections");

            migrationBuilder.DropColumn(
                name: "SkipLocations",
                table: "IntegrationConnections");

            migrationBuilder.DropColumn(
                name: "SkipAssets",
                table: "IntegrationConnections");

            migrationBuilder.DropColumn(
                name: "AutoUpdateAssetNames",
                table: "IntegrationConnections");

            migrationBuilder.DropColumn(
                name: "UpdateCompanyDetails",
                table: "IntegrationConnections");
        }
    }
}
