using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UniqueFrameworkVersionName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // [AEGIS-AUD-052] Guard de pré-condição. O CREATE UNIQUE INDEX já falharia diante de
            // duplicatas, mas com uma mensagem genérica do PostgreSQL. Aqui a falha explica o que
            // aconteceu e o que fazer.
            //
            // ⚠️ Deliberadamente NÃO há dedupe automático, diferente da migration de
            // GovernanceDocument.Sha256: lá as linhas repetidas eram o MESMO documento e podiam ser
            // descartadas com segurança. Um segundo catálogo NIST carrega functions, categories,
            // subcategories e, indiretamente, o TenantControlState que as referencia — escolher qual
            // versão sobrevive é decisão de operação, não de migration.
            migrationBuilder.Sql("""
                DO $$
                DECLARE duplicados int;
                BEGIN
                    SELECT count(*) INTO duplicados
                    FROM (
                        SELECT "Name" FROM "FrameworkVersions" GROUP BY "Name" HAVING count(*) > 1
                    ) d;

                    IF duplicados > 0 THEN
                        RAISE EXCEPTION
                            'AEGIS-AUD-052: % nome(s) duplicado(s) em FrameworkVersions. O indice unico '
                            'nao pode ser criado sobre dados duplicados, e esta migration NAO remove '
                            'registros. Consolide manualmente as versoes de catalogo (verificando as '
                            'referencias em TenantControlState) e execute a migration novamente.',
                            duplicados;
                    END IF;
                END $$;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_FrameworkVersions_Name",
                table: "FrameworkVersions",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FrameworkVersions_Name",
                table: "FrameworkVersions");
        }
    }
}
