using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <summary>
    /// [AEGIS-MVP-PRODUCT-03] A APLICABILIDADE ao ciclo vigente, congelada com cada ação do relatório.
    ///
    /// Sem estas colunas a fotografia guardava a validação mais recente e nada mais: uma ação validada,
    /// encerrada e depois REABERTA sem nova coleta ficava indistinguível de uma ação comprovada agora, e o
    /// total de "melhora comprovada" do relatório somava as duas. Recuperar a diferença na exportação
    /// exigiria ler o plano vivo — exatamente o que uma fotografia existe para dispensar.
    ///
    /// Todas as colunas são ADITIVAS e ANULÁVEIS: as fotografias já publicadas permanecem legíveis, o hash
    /// delas permanece verificável (a extensão canônica só é escrita quando há conteúdo) e o <c>null</c>
    /// significa "esta fotografia não congelou a distinção" — jamais "não se aplica ao ciclo".
    /// </summary>
    public partial class Product03_FrozenCycleApplicability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ApplicableValidatedAt",
                table: "PostureSnapshotActionItems",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ApplicableValidationMethod",
                table: "PostureSnapshotActionItems",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ApplicableValidationOutcome",
                table: "PostureSnapshotActionItems",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CycleStartedAt",
                table: "PostureSnapshotActionItems",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ValidationAppliesToCurrentCycle",
                table: "PostureSnapshotActionItems",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WasReopened",
                table: "PostureSnapshotActionItems",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApplicableValidatedAt",
                table: "PostureSnapshotActionItems");

            migrationBuilder.DropColumn(
                name: "ApplicableValidationMethod",
                table: "PostureSnapshotActionItems");

            migrationBuilder.DropColumn(
                name: "ApplicableValidationOutcome",
                table: "PostureSnapshotActionItems");

            migrationBuilder.DropColumn(
                name: "CycleStartedAt",
                table: "PostureSnapshotActionItems");

            migrationBuilder.DropColumn(
                name: "ValidationAppliesToCurrentCycle",
                table: "PostureSnapshotActionItems");

            migrationBuilder.DropColumn(
                name: "WasReopened",
                table: "PostureSnapshotActionItems");
        }
    }
}
