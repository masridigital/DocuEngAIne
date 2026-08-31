using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocuEngAIne.Api.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(DocuEngAIneDbContext))]
    [Migration("20260830210000_Phase2AssetDeepLinks")]
    public class Phase2AssetDeepLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalIdsJson",
                table: "Assets",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HaloAssetUrl",
                table: "Assets",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NinjaDeviceUrl",
                table: "Assets",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalIdsJson",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "HaloAssetUrl",
                table: "Assets");

            migrationBuilder.DropColumn(
                name: "NinjaDeviceUrl",
                table: "Assets");
        }
    }
}
