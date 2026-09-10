using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <summary>
    /// [AEGIS-ADM-02] A marca de CONTABILIZAÇÃO mensal da aquisição — uma coluna anulável, e nada além dela.
    ///
    /// Por que existe: o denominador histórico do mês não pode ser recalculado a partir das linhas que
    /// restam. As aquisições somem por dois motivos diferentes — a retenção deste pacote, que se contabiliza
    /// a si mesma, e a CASCATA da exclusão do conector, que leva a evidência sem passar por lugar nenhum.
    /// Um total definido como "sobreviventes + removidas pela retenção" desabava no segundo caso; um total
    /// ACUMULADO, com cada coleta entrando uma única vez, não desaba e continua absorvendo o que chegar.
    ///
    /// ADITIVA e sem backfill, de propósito. A coluna nasce nula, e nulo é exatamente "ainda não entrou em
    /// nenhum total" — que é a verdade em toda base onde as tabelas de consolidação acabaram de ser criadas
    /// pela migration anterior. A primeira manutenção contabiliza cada linha existente uma vez e carimba a
    /// marca; nenhuma migration anterior é reescrita, e NENHUM dado é apagado aqui.
    /// </summary>
    public partial class Adm02_MonthlyRollupAccounting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MonthlyRollupAccountedAt",
                table: "IdentityAcquisitions",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MonthlyRollupAccountedAt",
                table: "IdentityAcquisitions");
        }
    }
}
