using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <summary>
    /// [AEGIS-ADM-02] Historico MENSAL e retencao operacional do ADM de identidade. ADITIVA — nenhuma coluna
    /// existente muda de tipo ou de nulidade, nenhuma migration anterior e reescrita e NENHUM DADO E REMOVIDO:
    ///   • 2 tabelas novas, tenant-owned: a CONSOLIDACAO mensal por origem e os valores POR CONJUNTO da
    ///     fotografia daquele mes;
    ///   • FK COMPOSTA tenant-safe (RollupId, TenantId) → IdentityMonthlyRollups (Id, TenantId), com Cascade:
    ///     o proprio banco recusa filho de tenant divergente, e os valores por conjunto nao existem sem a
    ///     consolidacao a que pertencem;
    ///   • 2 indices UNICOS:
    ///       UX_IdentityMonthlyRollup_Natural    (TenantId, Provider, DirectoryNamespace, Month) — uma linha
    ///         por mes e por origem; e o que torna a reconsolidacao idempotente uma invariante de BANCO, e nao
    ///         uma promessa do read-then-write;
    ///       UX_IdentityMonthlyRollupSet_Natural (TenantId, RollupId, Set) — um valor por conjunto.
    ///   • 1 coluna ANULAVEL DetailRetiredAt em IdentityAcquisitions: o carimbo de "detalhe expirado por
    ///     retencao". NULL em toda linha existente, e e exatamente isso que ela deve significar — as
    ///     aquisicoes ja gravadas continuam com o detalhe que sempre tiveram;
    ///   • 1 coluna AcquiredAtUtc (o MESMO instante de AcquiredAt, na forma comparavel e ordenavel em todos os
    ///     provedores) + indice de varredura por janela. E DERIVADA, e o backfill abaixo a calcula a partir da
    ///     coluna que ja existe — nao ha dado novo, nem dado alterado.
    ///
    /// ⚠️ Deliberadamente SEM chave estrangeira da consolidacao para IdentityAcquisitions: a aquisicao tem
    /// prazo de retencao proprio (90 dias para o detalhe; remocao integral quando nada mais a referencia), e
    /// uma FK obrigaria a escolher entre travar a retencao e destruir o historico de 12 meses. A consolidacao
    /// carrega a propria copia agregada da proveniencia, e por isso sobrevive sozinha.
    ///
    /// ⚠️ Esta migration NAO executa expurgo. A retencao e trabalho da manutencao em fundo, roda em lotes com
    /// decisao dentro da transacao e nasce DESABILITADA por configuracao. Migration que apaga dados apagaria
    /// tambem em ambientes onde ninguem autorizou nada — e sem chance de conferir o que sairia antes.
    /// </summary>
    public partial class Adm02_IdentityMonthlyHistoryRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AcquiredAtUtc",
                table: "IdentityAcquisitions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // Backfill da chave DERIVADA a partir da coluna que ja existe. Nao inventa informacao: copia o
            // instante ja gravado para a forma que a varredura consegue filtrar, agrupar e ordenar.
            //
            // ⚠️ O filtro compara com o VALOR CORRETO, e nao com a sentinela do default. O Npgsql converte
            // DateTime.MinValue para o literal '-infinity' do PostgreSQL, de modo que um
            // `WHERE "AcquiredAtUtc" = TIMESTAMPTZ '0001-01-01'` nao casaria com uma unica linha — e o
            // backfill passaria silenciosamente, deixando toda a evidencia antiga com data do ano 1 (varrida,
            // no primeiro ciclo de retencao, como se estivesse vencida ha dois mil anos). Comparar com o valor
            // derivado torna a instrucao idempotente sem depender de como a sentinela ficou gravada: uma
            // reexecucao do migrator sobre um banco ja atualizado nao toca em nenhuma linha.
            migrationBuilder.Sql(
                "UPDATE \"IdentityAcquisitions\" "
                + "SET \"AcquiredAtUtc\" = \"AcquiredAt\" "
                + "WHERE \"AcquiredAtUtc\" IS DISTINCT FROM \"AcquiredAt\";");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DetailRetiredAt",
                table: "IdentityAcquisitions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "IdentityMonthlyRollups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<int>(type: "integer", nullable: false),
                    DirectoryNamespace = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Month = table.Column<DateOnly>(type: "date", nullable: false),
                    IsProvisional = table.Column<bool>(type: "boolean", nullable: false),
                    AcquisitionCount = table.Column<int>(type: "integer", nullable: false),
                    DataProducingCount = table.Column<int>(type: "integer", nullable: false),
                    RetiredAcquisitionCount = table.Column<int>(type: "integer", nullable: false),
                    RetiredDataProducingCount = table.Column<int>(type: "integer", nullable: false),
                    RetentionSweptThroughAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SnapshotAcquisitionId = table.Column<Guid>(type: "uuid", nullable: true),
                    SnapshotAcquiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SnapshotObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SnapshotConnectorConfigId = table.Column<Guid>(type: "uuid", nullable: true),
                    SnapshotSourceLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SnapshotSchemaVersion = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    SnapshotNormalizationVersion = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    SnapshotState = table.Column<int>(type: "integer", nullable: true),
                    LastAttemptAcquisitionId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastAttemptState = table.Column<int>(type: "integer", nullable: true),
                    LastAttemptDetail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ConsolidatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsolidationVersion = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityMonthlyRollups", x => x.Id);
                    table.UniqueConstraint("AK_IdentityMonthlyRollups_Id_TenantId", x => new { x.Id, x.TenantId });
                });

            migrationBuilder.CreateTable(
                name: "IdentityMonthlyRollupSets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RollupId = table.Column<Guid>(type: "uuid", nullable: false),
                    Set = table.Column<int>(type: "integer", nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    ObservedCount = table.Column<int>(type: "integer", nullable: false),
                    PreservedCount = table.Column<int>(type: "integer", nullable: false),
                    IsComplete = table.Column<bool>(type: "boolean", nullable: false),
                    Limitation = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityMonthlyRollupSets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IdentityMonthlyRollupSets_IdentityMonthlyRollups_RollupId_T~",
                        columns: x => new { x.RollupId, x.TenantId },
                        principalTable: "IdentityMonthlyRollups",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityAcquisitions_TenantId_DirectoryNamespace_AcquiredAt~",
                table: "IdentityAcquisitions",
                columns: new[] { "TenantId", "DirectoryNamespace", "AcquiredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityMonthlyRollups_TenantId_Month",
                table: "IdentityMonthlyRollups",
                columns: new[] { "TenantId", "Month" });

            migrationBuilder.CreateIndex(
                name: "UX_IdentityMonthlyRollup_Natural",
                table: "IdentityMonthlyRollups",
                columns: new[] { "TenantId", "Provider", "DirectoryNamespace", "Month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityMonthlyRollupSets_RollupId_TenantId",
                table: "IdentityMonthlyRollupSets",
                columns: new[] { "RollupId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "UX_IdentityMonthlyRollupSet_Natural",
                table: "IdentityMonthlyRollupSets",
                columns: new[] { "TenantId", "RollupId", "Set" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IdentityMonthlyRollupSets");

            migrationBuilder.DropTable(
                name: "IdentityMonthlyRollups");

            migrationBuilder.DropIndex(
                name: "IX_IdentityAcquisitions_TenantId_DirectoryNamespace_AcquiredAt~",
                table: "IdentityAcquisitions");

            migrationBuilder.DropColumn(
                name: "AcquiredAtUtc",
                table: "IdentityAcquisitions");

            migrationBuilder.DropColumn(
                name: "DetailRetiredAt",
                table: "IdentityAcquisitions");
        }
    }
}
