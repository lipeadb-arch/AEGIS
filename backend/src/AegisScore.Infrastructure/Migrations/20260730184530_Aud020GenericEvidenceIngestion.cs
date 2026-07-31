using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Aud020GenericEvidenceIngestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeduplicationKey",
                table: "Signals",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EventType",
                table: "Signals",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalEventId",
                table: "Signals",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProtectedRawPayload",
                table: "Signals",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReceivedAt",
                table: "Signals",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SchemaVersion",
                table: "Signals",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "Signals",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "SignalKey",
                table: "SignalMappings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "IngestionKeyHash",
                table: "Connectors",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_EvidenceSignal_Idempotency",
                table: "Signals",
                columns: new[] { "TenantId", "ConnectorConfigId", "DeduplicationKey" },
                unique: true,
                filter: "\"DeduplicationKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SignalMappings_FrameworkVersionId_Capability_SignalKey",
                table: "SignalMappings",
                columns: new[] { "FrameworkVersionId", "Capability", "SignalKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_EvidenceSignal_Idempotency",
                table: "Signals");

            migrationBuilder.DropIndex(
                name: "IX_SignalMappings_FrameworkVersionId_Capability_SignalKey",
                table: "SignalMappings");

            migrationBuilder.DropColumn(
                name: "DeduplicationKey",
                table: "Signals");

            migrationBuilder.DropColumn(
                name: "EventType",
                table: "Signals");

            migrationBuilder.DropColumn(
                name: "ExternalEventId",
                table: "Signals");

            migrationBuilder.DropColumn(
                name: "ProtectedRawPayload",
                table: "Signals");

            migrationBuilder.DropColumn(
                name: "ReceivedAt",
                table: "Signals");

            migrationBuilder.DropColumn(
                name: "SchemaVersion",
                table: "Signals");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "Signals");

            migrationBuilder.DropColumn(
                name: "IngestionKeyHash",
                table: "Connectors");

            migrationBuilder.AlterColumn<string>(
                name: "SignalKey",
                table: "SignalMappings",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);
        }
    }
}
