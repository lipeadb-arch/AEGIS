using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <summary>
    /// [AEGIS-ENTITY-RESOLUTION-01] Migration ADITIVA (três colunas anuláveis, sem backfill), posterior à
    /// <c>DeviceResolution01_StrongIdentifiers</c> — que não é reescrita:
    ///   • <c>Connectors.DeviceSnapshotWatermark</c> — marca da fotografia de dispositivos mais recente publicada pela
    ///     fonte (precedência entre passadas do mesmo conector). Nula = nenhuma publicada ainda: a próxima passada a
    ///     define; nada é presumido para conectores existentes;
    ///   • <c>AssetSourceBindings.ConflictDirectoryNamespace</c> — diretório da observação que causou o conflito, separado
    ///     do vínculo estabelecido. Conflitos anteriores ficam NULOS (o diretório daquela observação não foi guardado e
    ///     não é inventado);
    ///   • <c>AssetSourceBindings.ConflictObservedDeviceIds</c> — identificadores contraditórios da mesma coleta.
    /// Nenhum dado é apagado, movido ou reescrito.
    /// </summary>
    public partial class DeviceResolution02_LifecycleAndConflictProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeviceSnapshotWatermark",
                table: "Connectors",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConflictDirectoryNamespace",
                table: "AssetSourceBindings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConflictObservedDeviceIds",
                table: "AssetSourceBindings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeviceSnapshotWatermark",
                table: "Connectors");

            migrationBuilder.DropColumn(
                name: "ConflictDirectoryNamespace",
                table: "AssetSourceBindings");

            migrationBuilder.DropColumn(
                name: "ConflictObservedDeviceIds",
                table: "AssetSourceBindings");
        }
    }
}
