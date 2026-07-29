using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Aud010NullablePasswordHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "PasswordHash",
                table: "IdentityAccounts",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // [AEGIS-AUD-010] Rollback SEGURO. Reexigir NOT NULL só é possível se NENHUMA conta
            // federated-only (PasswordHash NULL — identidade global sem credencial local) existir. Do
            // contrário, reverter significaria INVENTAR uma credencial (string vazia/hash fictício) ou
            // APAGAR contas — ambos proibidos. Se houver conta passwordless, ABORTA com mensagem explícita,
            // preservando 100% dos dados; o operador deve resolver essas contas antes de reverter. Mesmo
            // idioma do bloco DO $$ que protege a NormalizeIdentityAccount (§22.3): o gerador do EF é
            // ordenador ingênuo, não migrador de dados — o defaultValue "" que ele produziria corromperia
            // silenciosamente cada conta federada.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM ""IdentityAccounts"" WHERE ""PasswordHash"" IS NULL) THEN
        RAISE EXCEPTION 'Rollback do AUD-010 bloqueado: existem contas federated-only sem PasswordHash. Reverter para NOT NULL exigiria inventar credencial ou apagar contas (ambos proibidos). Resolva essas contas (defina credencial local ou remova-as deliberadamente) antes de reverter.';
    END IF;
    ALTER TABLE ""IdentityAccounts"" ALTER COLUMN ""PasswordHash"" SET NOT NULL;
END $$;");
        }
    }
}
