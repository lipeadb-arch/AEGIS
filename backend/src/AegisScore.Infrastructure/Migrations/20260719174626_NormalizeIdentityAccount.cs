using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeIdentityAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ⚠️ ORDEM REESCRITA À MÃO. O scaffold do EF derrubava "Email"/"PasswordHash" ANTES de criar
            // a tabela de contas — o que apagaria TODA credencial do sistema antes de haver para onde
            // copiá-la. A sequência correta é: criar o destino → copiar → só então remover a origem.

            migrationBuilder.AddColumn<Guid>(
                name: "IdentityAccountId",
                table: "Users",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "IdentityAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityAccounts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityAccounts_Email",
                table: "IdentityAccounts",
                column: "Email",
                unique: true);

            // ================= BACKFILL (data migration) =================
            // Fusão por e-mail: no modelo do MSSP, o e-mail corporativo é a MESMA pessoa física em todos
            // os clientes. Uma conta por e-mail DISTINTO; quando o mesmo e-mail tinha hashes divergentes
            // entre tenants, elege-se o do registro mais RECENTE (COALESCE(UpdatedAt, CreatedAt)).
            //
            // ⚠️ Consequência aceita conscientemente: a senha eleita passa a abrir ambientes que ela não
            // abria antes, e as demais senhas daquele e-mail deixam de valer. Quem tinha senhas distintas
            // por cliente precisa usar a mais recente. Decisão registrada na §22 do AEGIS_STATE.
            //
            // LOWER() no agrupamento: o serviço normaliza o e-mail, mas linhas semeadas à mão podem ter
            // caixa mista — agrupar sem normalizar criaria duas contas para a mesma pessoa e o índice
            // único global rejeitaria a segunda.
            migrationBuilder.Sql(@"
                INSERT INTO ""IdentityAccounts"" (""Id"", ""Email"", ""PasswordHash"", ""CreatedAt"", ""UpdatedAt"")
                SELECT gen_random_uuid(), e.email, e.""PasswordHash"", now(), NULL
                FROM (
                    SELECT DISTINCT ON (LOWER(""Email""))
                           LOWER(""Email"") AS email,
                           ""PasswordHash""
                    FROM ""Users""
                    ORDER BY LOWER(""Email""), COALESCE(""UpdatedAt"", ""CreatedAt"") DESC, ""Id"" DESC
                ) e;");

            migrationBuilder.Sql(@"
                UPDATE ""Users"" u
                SET ""IdentityAccountId"" = a.""Id""
                FROM ""IdentityAccounts"" a
                WHERE a.""Email"" = LOWER(u.""Email"");");

            // ⚠️ Rede de segurança: se alguma linha ficou órfã (e-mail nulo/vazio, algo inesperado), o
            // INSERT do índice/FK abaixo falharia com uma mensagem obscura. Falhar AQUI, com texto claro
            // e dentro da transação da migration, é infinitamente melhor que um banco meio migrado.
            migrationBuilder.Sql(@"
                DO $$
                DECLARE orfas INT;
                BEGIN
                    SELECT COUNT(*) INTO orfas FROM ""Users""
                    WHERE ""IdentityAccountId"" = '00000000-0000-0000-0000-000000000000';
                    IF orfas > 0 THEN
                        RAISE EXCEPTION
                            'Backfill de identidade incompleto: % linha(s) de Users sem IdentityAccount.', orfas;
                    END IF;
                END $$;");

            // ================= Só agora a origem pode sair =================
            migrationBuilder.DropIndex(
                name: "IX_Users_TenantId_Email",
                table: "Users");

            migrationBuilder.DropColumn(name: "Email", table: "Users");
            migrationBuilder.DropColumn(name: "PasswordHash", table: "Users");

            migrationBuilder.CreateIndex(
                name: "IX_Users_IdentityAccountId",
                table: "Users",
                column: "IdentityAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_TenantId_IdentityAccountId",
                table: "Users",
                columns: new[] { "TenantId", "IdentityAccountId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Users_IdentityAccounts_IdentityAccountId",
                table: "Users",
                column: "IdentityAccountId",
                principalTable: "IdentityAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ⚠️ ORDEM REESCRITA À MÃO, pelo mesmo motivo do Up(): o scaffold dropava IdentityAccounts
            // ANTES de devolver e-mail/hash às linhas de Users — um rollback deixaria todo mundo com
            // credencial em branco. Restaura primeiro, remove a origem depois.
            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "Users",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PasswordHash",
                table: "Users",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(@"
                UPDATE ""Users"" u
                SET ""Email"" = a.""Email"", ""PasswordHash"" = a.""PasswordHash""
                FROM ""IdentityAccounts"" a
                WHERE a.""Id"" = u.""IdentityAccountId"";");

            migrationBuilder.DropForeignKey(
                name: "FK_Users_IdentityAccounts_IdentityAccountId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_IdentityAccountId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_TenantId_IdentityAccountId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IdentityAccountId",
                table: "Users");

            migrationBuilder.DropTable(
                name: "IdentityAccounts");

            migrationBuilder.CreateIndex(
                name: "IX_Users_TenantId_Email",
                table: "Users",
                columns: new[] { "TenantId", "Email" },
                unique: true);
        }
    }
}
