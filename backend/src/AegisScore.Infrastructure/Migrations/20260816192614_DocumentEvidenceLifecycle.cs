using System;
using AegisScore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <summary>
    /// Rastreabilidade da evidência documental + REPARO idempotente da homologação. Adiciona a persistência
    /// mínima da reconciliação determinística (trecho literal por mapping; origem documental por estado do
    /// ledger) e, no MESMO passo, invalida os derivados documentais LEGADOS que não têm prova literal — o
    /// que faz o estado órfão de Govern (ex.: 40% sem documento válido) desaparecer no próximo deploy, sem
    /// SQL manual no banco.
    ///
    /// Aplicado pelo <c>AegisScore.DbMigrator</c> (PostgreSQL). Os testes rodam sobre SQLite via
    /// <c>EnsureCreated</c> — este SQL de reparo não roda lá; a MESMA lógica é coberta em C# pela
    /// reconciliação (exclusão/reanálise) sobre SQLite.
    /// </summary>
    public partial class DocumentEvidenceLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Persistência mínima da reconciliação determinística.
            migrationBuilder.AddColumn<Guid>(
                name: "OriginDocumentId",
                table: "TenantControlStates",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvidenceQuote",
                table: "DocumentControlMappings",
                type: "text",
                nullable: true);

            // 2) REPARO idempotente (determinístico e seguro em banco vazio — os WHERE não casam nada). O SQL
            //    vive em DocumentEvidenceRepair para que o teste focado exercite EXATAMENTE estas instruções:
            //    (a) retrai estado documental legado sem prova literal (telemetria preservada); (b) remove
            //    cobertura só-documental sem prova; (c) Both sem prova → Interview (entrevista intacta);
            //    (d) re-enfileira documentos existentes com binário para reanálise pelo novo pipeline. Como a
            //    coluna EvidenceQuote nasce NULL, a 1ª aplicação retrai TODO derivado documental legado — é o
            //    que faz o estado órfão de Govern desaparecer no próximo deploy.
            foreach (var sql in DocumentEvidenceRepair.Statements)
                migrationBuilder.Sql(sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // O REPARO de dados (retração de estados/cobertura legados) é IRREVERSÍVEL por natureza — não há
            // como recriar uma prova que nunca existiu. O Down reverte apenas o schema (as duas colunas);
            // os estados retraídos permanecem retraídos, o que é o comportamento correto e seguro.
            migrationBuilder.DropColumn(
                name: "OriginDocumentId",
                table: "TenantControlStates");

            migrationBuilder.DropColumn(
                name: "EvidenceQuote",
                table: "DocumentControlMappings");
        }
    }
}
