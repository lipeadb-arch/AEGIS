using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Aud011SeparatePlatformTenantRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Eixo GLOBAL na identidade (default de banco None=0). Coluna aditiva, NOT NULL com default —
            //    linhas existentes recebem 0 e inserts que não citam a coluna também.
            migrationBuilder.AddColumn<int>(
                name: "PlatformRole",
                table: "IdentityAccounts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // 2) Integridade + BACKFILL, ANTES de impor a constraint do eixo tenant (senão ela rejeitaria o
            //    valor legado 3). Fail-closed: um Users.Role fora de 0..3 é desconhecido e ABORTA com
            //    mensagem clara — não inventamos mapeamento.
            //
            //    Regra do backfill (corrigida): a autoridade global só é ATIVÁVEL se, antes do AUD-011, o
            //    privilégio legado era USÁVEL — isto é, existia um membership Role=3 ATIVO num tenant NÃO
            //    suspenso. Um Role=3 inativo, ou de tenant Suspended, NÃO vira PlatformAdmin global (senão
            //    um privilégio adormecido seria reativado, ainda mais se a identidade tiver outro membership
            //    ativo). Só então (passo 2b) todos os Role=3 são normalizados para TenantAdmin (2) —
            //    inclusive inativos/suspensos —, preservando o acesso operacional sem espalhar autoridade.
            //    Status do tenant: Onboarding=0, Active=1, Suspended=2 (só o 2 desqualifica).
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM ""Users"" WHERE ""Role"" NOT IN (0, 1, 2, 3)) THEN
        RAISE EXCEPTION 'AUD-011: Users.Role contém valor(es) desconhecido(s) fora de 0..3. Corrija os memberships antes de migrar (não há mapeamento seguro para valores fora da faixa).';
    END IF;
END $$;

UPDATE ""IdentityAccounts"" a
   SET ""PlatformRole"" = 1
 WHERE ""PlatformRole"" = 0
   AND EXISTS (
       SELECT 1
         FROM ""Users"" u
         JOIN ""Tenants"" t ON t.""Id"" = u.""TenantId""
        WHERE u.""IdentityAccountId"" = a.""Id""
          AND u.""Role"" = 3
          AND u.""IsActive"" = TRUE
          AND t.""Status"" <> 2
   );

UPDATE ""Users"" SET ""Role"" = 2 WHERE ""Role"" = 3;");

            // 3) Constraints de banco dos DOIS eixos, agora que os dados estão dentro da faixa.
            migrationBuilder.AddCheckConstraint(
                name: "CK_Users_Role",
                table: "Users",
                sql: "\"Role\" IN (0, 1, 2)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_IdentityAccounts_PlatformRole",
                table: "IdentityAccounts",
                sql: "\"PlatformRole\" IN (0, 1)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // [AEGIS-AUD-011] Rollback FAIL-CLOSED. O modelo antigo representava a autoridade global como
            // Users.Role=3 (PlatformAdmin) — um valor de MEMBERSHIP. Reverter uma IdentityAccount com
            // PlatformRole<>None exigiria escolher UM membership e gravar 3 nele, ESPALHANDO autoridade
            // global por um tenant (exatamente o que este pacote elimina) ou DESCARTAR a autoridade. Ambos
            // proibidos: se houver papel global atribuído, ABORTA com mensagem explícita, preservando 100%
            // dos dados. Sem papel global (todos None), o rollback é seguro — não inventa permissão, não
            // espalha PlatformAdmin e não apaga identidades/memberships.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM ""IdentityAccounts"" WHERE ""PlatformRole"" <> 0) THEN
        RAISE EXCEPTION 'Rollback do AUD-011 bloqueado: existem identidades com papel global (PlatformRole <> None). Representá-las no modelo antigo exigiria espalhar PlatformAdmin para um membership (proibido) ou descartar a autoridade global. Rebaixe esses papéis globais (PlatformRole=None) deliberadamente antes de reverter.';
    END IF;
END $$;");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Users_Role",
                table: "Users");

            migrationBuilder.DropCheckConstraint(
                name: "CK_IdentityAccounts_PlatformRole",
                table: "IdentityAccounts");

            migrationBuilder.DropColumn(
                name: "PlatformRole",
                table: "IdentityAccounts");
        }
    }
}
