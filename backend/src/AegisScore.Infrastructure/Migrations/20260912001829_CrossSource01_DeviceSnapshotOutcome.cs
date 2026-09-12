using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <summary>
    /// [AEGIS-CROSS-SOURCE-01] Migration ADITIVA (uma coluna, sem backfill), posterior à
    /// <c>DeviceResolution02_LifecycleAndConflictProvenance</c> — que não é reescrita:
    ///   • <c>Connectors.DeviceSnapshotOutcome</c> — desfecho da publicação da fotografia de dispositivos marcada em
    ///     <c>DeviceSnapshotWatermark</c> (1 = publicando, 2 = completa, 3 = parcial). O valor 0 ("não registrado") vale
    ///     para todo conector existente: o desfecho das fotografias já publicadas não foi guardado e não é inventado —
    ///     a próxima passada de cada fonte o registra.
    /// Necessária porque a leitura das situações entre fontes precisa distinguir fato da aquisição atual completa,
    /// parcial ou ainda não concluída; o status geral do conector não comprova a completude de uma dimensão.
    /// Nenhum dado é apagado, movido ou reescrito.
    /// </summary>
    public partial class CrossSource01_DeviceSnapshotOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DeviceSnapshotOutcome",
                table: "Connectors",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeviceSnapshotOutcome",
                table: "Connectors");
        }
    }
}
