using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <summary>
    /// [AEGIS-AUD-009] Passa a persistir apenas o HASH SHA-256 dos refresh tokens.
    ///
    /// Estratégia PRESERVA sessões existentes (sem novo login forçado): o <c>Up</c> transforma os valores
    /// brutos de <c>Token</c>/<c>ReplacedByToken</c> em SHA-256 hexadecimal ANTES de renomear as colunas,
    /// usando <c>encode(sha256(convert_to(v,'UTF8')),'hex')</c>. Essa função é NATIVA do PostgreSQL desde
    /// a versão 11 (não exige a extensão pgcrypto) e produz exatamente o mesmo hash que o
    /// <c>Sha256RefreshTokenHasher</c> do backend — validado em PostgreSQL descartável real antes desta
    /// migration. Um token bruto vira o hash pelo qual o serviço passará a procurá-lo; a cadeia de rotação
    /// (pai→sucessor) continua íntegra porque pai.ReplacedBy e filho.Token hasheiam para o mesmo valor.
    ///
    /// O <c>Down</c> NÃO restaura plaintext (é impossível a partir do hash). Rollback invalida as sessões:
    /// remove as linhas de refresh — e SOMENTE elas — antes de voltar aos nomes/larguras antigos. Usuários,
    /// memberships e contas permanecem intactos; o efeito operacional é um novo login para todos.
    /// </summary>
    public partial class Aud009HashRefreshTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Backfill determinístico dos valores existentes (ainda sob os nomes antigos, cabendo nos
            //    varchar(512)). NULL em ReplacedByToken permanece NULL. Índice único (TenantId, Token)
            //    continua válido: SHA-256 é injetivo sobre os brutos, que já eram únicos.
            migrationBuilder.Sql(
                """
                UPDATE "UserRefreshTokens"
                SET "Token" = encode(sha256(convert_to("Token", 'UTF8')), 'hex'),
                    "ReplacedByToken" = CASE
                        WHEN "ReplacedByToken" IS NOT NULL
                        THEN encode(sha256(convert_to("ReplacedByToken", 'UTF8')), 'hex')
                        ELSE NULL
                    END;
                """);

            // 2) Renomeia colunas e índice para a semântica de hash (preserva os dados já transformados).
            migrationBuilder.RenameColumn(
                name: "Token",
                table: "UserRefreshTokens",
                newName: "TokenHash");

            migrationBuilder.RenameColumn(
                name: "ReplacedByToken",
                table: "UserRefreshTokens",
                newName: "ReplacedByTokenHash");

            migrationBuilder.RenameIndex(
                name: "IX_UserRefreshTokens_TenantId_Token",
                table: "UserRefreshTokens",
                newName: "IX_UserRefreshTokens_TenantId_TokenHash");

            // 3) Estreita para 64 chars — a largura exata do hash hex, agora uma invariante de banco.
            migrationBuilder.AlterColumn<string>(
                name: "TokenHash",
                table: "UserRefreshTokens",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(512)",
                oldMaxLength: 512,
                oldNullable: false);

            migrationBuilder.AlterColumn<string>(
                name: "ReplacedByTokenHash",
                table: "UserRefreshTokens",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(512)",
                oldMaxLength: 512,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rollback invalida sessões: o plaintext não é recuperável a partir do hash, então apaga apenas
            // as linhas de refresh (nunca usuários/memberships/contas) e volta ao esquema anterior VAZIO.
            // Efeito operacional: todos precisam logar de novo.
            migrationBuilder.Sql("DELETE FROM \"UserRefreshTokens\";");

            migrationBuilder.RenameIndex(
                name: "IX_UserRefreshTokens_TenantId_TokenHash",
                table: "UserRefreshTokens",
                newName: "IX_UserRefreshTokens_TenantId_Token");

            migrationBuilder.RenameColumn(
                name: "TokenHash",
                table: "UserRefreshTokens",
                newName: "Token");

            migrationBuilder.RenameColumn(
                name: "ReplacedByTokenHash",
                table: "UserRefreshTokens",
                newName: "ReplacedByToken");

            migrationBuilder.AlterColumn<string>(
                name: "Token",
                table: "UserRefreshTokens",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: false);

            migrationBuilder.AlterColumn<string>(
                name: "ReplacedByToken",
                table: "UserRefreshTokens",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);
        }
    }
}
