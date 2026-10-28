using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <summary>
    /// [AEGIS-KNIGHT-PERSISTENCE-01] Motivo de "não avaliado" (execução e fotografia), evidência e limitação do detalhe
    /// congeladas passam de character varying(n) para text. No PostgreSQL a conversão de varchar para text é binariamente
    /// compatível: não reescreve a tabela e preserva cada linha exatamente como estava. O Down volta aos limites antigos e,
    /// de propósito, FALHA se já houver texto maior que eles — recusa em vez de cortar o que foi gravado.
    /// </summary>
    public partial class KnightPersistence01_DiagnosticTextColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "NotEvaluatedReason",
                table: "PostureSnapshotIndicators",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Evidence",
                table: "PostureSnapshotIndicators",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000);

            migrationBuilder.AlterColumn<string>(
                name: "AffectedDetailLimitation",
                table: "PostureSnapshotIndicators",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "NotEvaluatedReason",
                table: "KnightIndicatorResults",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "NotEvaluatedReason",
                table: "PostureSnapshotIndicators",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Evidence",
                table: "PostureSnapshotIndicators",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "AffectedDetailLimitation",
                table: "PostureSnapshotIndicators",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "NotEvaluatedReason",
                table: "KnightIndicatorResults",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
