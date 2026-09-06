using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <summary>
    /// [AEGIS-MVP-PRODUCT-02] Preserva os OBJETOS que sustentam um achado do KNIGHT. Menor alteracao de schema
    /// possivel e ESTRITAMENTE ADITIVA:
    ///   • 3 colunas em KnightIndicatorResults com default seguro (false/null) — execucoes ja gravadas
    ///     permanecem intactas e passam a se declarar "sem detalhe preservado"; NADA e retropreenchido com a
    ///     coleta atual, que seria apresentar o presente como prova do passado;
    ///   • 1 tabela KnightAffectedObjects, tenant-owned, com FK COMPOSTA (IndicatorResultId, TenantId) contra a
    ///     chave alternativa (Id, TenantId) do resultado — o proprio banco recusa um afetado de tenant
    ///     divergente, e o objeto nao existe sem o resultado (Cascade);
    ///   • indice unico (TenantId, IndicatorResultId, ExternalId): a deduplicacao do coletor vira invariante de
    ///     banco, de modo que lista e contagem nao possam divergir por duplicata.
    /// Nenhuma coluna existente muda de tipo ou de nulidade; nenhum dado antigo e reescrito ou apagado.
    /// </summary>
    public partial class Product02_KnightAffectedObjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AffectedDetailComplete",
                table: "KnightIndicatorResults",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AffectedDetailLimitation",
                table: "KnightIndicatorResults",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasAffectedDetail",
                table: "KnightIndicatorResults",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_KnightIndicatorResults_Id_TenantId",
                table: "KnightIndicatorResults",
                columns: new[] { "Id", "TenantId" });

            migrationBuilder.CreateTable(
                name: "KnightAffectedObjects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    IndicatorResultId = table.Column<Guid>(type: "uuid", nullable: false),
                    IndicatorId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    UserPrincipalName = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    Roles = table.Column<string>(type: "jsonb", nullable: false),
                    Detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnightAffectedObjects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KnightAffectedObjects_KnightIndicatorResults_IndicatorResul~",
                        columns: x => new { x.IndicatorResultId, x.TenantId },
                        principalTable: "KnightIndicatorResults",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KnightAffectedObjects_IndicatorResultId_TenantId",
                table: "KnightAffectedObjects",
                columns: new[] { "IndicatorResultId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_KnightAffectedObjects_TenantId_IndicatorResultId_ExternalId",
                table: "KnightAffectedObjects",
                columns: new[] { "TenantId", "IndicatorResultId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KnightAffectedObjects_TenantId_RunId_IndicatorId",
                table: "KnightAffectedObjects",
                columns: new[] { "TenantId", "RunId", "IndicatorId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KnightAffectedObjects");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_KnightIndicatorResults_Id_TenantId",
                table: "KnightIndicatorResults");

            migrationBuilder.DropColumn(
                name: "AffectedDetailComplete",
                table: "KnightIndicatorResults");

            migrationBuilder.DropColumn(
                name: "AffectedDetailLimitation",
                table: "KnightIndicatorResults");

            migrationBuilder.DropColumn(
                name: "HasAffectedDetail",
                table: "KnightIndicatorResults");
        }
    }
}
