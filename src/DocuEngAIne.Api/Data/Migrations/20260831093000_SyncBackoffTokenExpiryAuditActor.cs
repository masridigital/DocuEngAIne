using System;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocuEngAIne.Api.Data.Migrations
{
    /// <summary>
    /// Three review-driven columns:
    /// <c>IntegrationConnections.LastAttemptAt</c> — sync cadence keys off attempts as well as
    /// successes, so a failing connection backs off for its interval instead of retrying every
    /// scheduler tick and burning the StackJack allowance;
    /// <c>ApiTokens.ExpiresAt</c> — optional hard expiry checked at authentication;
    /// <c>AuditLogs.ActorObjectId</c> — the actor in its own terms (Entra object id,
    /// <c>apitoken:{id}</c>, <c>system:sync-scheduler</c>), because <c>UserId</c> resolves only
    /// for Entra browser users.
    /// </summary>
    [DbContext(typeof(DocuEngAIneDbContext))]
    [Migration("20260831093000_SyncBackoffTokenExpiryAuditActor")]
    public class SyncBackoffTokenExpiryAuditActor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastAttemptAt",
                table: "IntegrationConnections",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiresAt",
                table: "ApiTokens",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActorObjectId",
                table: "AuditLogs",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastAttemptAt",
                table: "IntegrationConnections");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "ApiTokens");

            migrationBuilder.DropColumn(
                name: "ActorObjectId",
                table: "AuditLogs");
        }
    }
}
