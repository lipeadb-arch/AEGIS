using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <summary>
    /// [AEGIS-ADM-02] Comprovante ESPECÍFICO de que a retenção removeu a aquisição da fotografia do mês.
    ///
    /// A leitura atribuía a remoção à retenção comparando o instante da fotografia com a fronteira varrida do
    /// mês — e a fronteira avança por outras coletas enquanto uma fotografia PROTEGIDA é pulada, de modo que
    /// uma cascata posterior (exclusão do conector) era apresentada como expurgo. Duas colunas anuláveis por
    /// mês, gravadas na mesma transação da remoção. ADITIVA e SEM backfill: não há como provar, a posteriori,
    /// a causa de uma ausência antiga — e nulo é justamente "sem comprovante".
    /// </summary>
    public partial class Adm02_SnapshotRetentionRemovalProof : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RetentionRemovedSnapshotAcquisitionId",
                table: "IdentityMonthlyRollups",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetentionRemovedSnapshotAt",
                table: "IdentityMonthlyRollups",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RetentionRemovedSnapshotAcquisitionId",
                table: "IdentityMonthlyRollups");

            migrationBuilder.DropColumn(
                name: "RetentionRemovedSnapshotAt",
                table: "IdentityMonthlyRollups");
        }
    }
}
