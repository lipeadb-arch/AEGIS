using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <summary>
    /// [AEGIS-ASSESSMENT-VISUALS-01] ADITIVA: histórico mensal congelado na publicação. Coluna anulável — as fotografias
    /// existentes ficam com NULL, mantêm o hash e continuam exportadas exatamente como antes. ADD COLUMN é DDL e não aciona
    /// o gatilho de imutabilidade (que recusa UPDATE/DELETE de linhas).
    /// </summary>
    public partial class AssessmentVisuals01_FrozenHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HistoryJson",
                table: "PostureSnapshots",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HistoryJson",
                table: "PostureSnapshots");
        }
    }
}
