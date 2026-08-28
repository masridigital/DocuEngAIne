using System;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocuEngAIne.Api.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(DocuEngAIneDbContext))]
    [Migration("20260828060000_Phase2StackJackPlan")]
    public class Phase2StackJackPlan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // StackJack meters per connector subscription, not account-wide, so the tier and its
            // allowance belong on the connection rather than on the shared McpServer registration.
            migrationBuilder.AddColumn<int>(
                name: "StackJackPlan",
                table: "IntegrationConnections",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MonthlyCallLimit",
                table: "IntegrationConnections",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PlanDetectedAt",
                table: "IntegrationConnections",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SyncIntervalMinutesOverride",
                table: "IntegrationConnections",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "StackJackPlan", table: "IntegrationConnections");
            migrationBuilder.DropColumn(name: "MonthlyCallLimit", table: "IntegrationConnections");
            migrationBuilder.DropColumn(name: "PlanDetectedAt", table: "IntegrationConnections");
            migrationBuilder.DropColumn(name: "SyncIntervalMinutesOverride", table: "IntegrationConnections");
        }
    }
}
