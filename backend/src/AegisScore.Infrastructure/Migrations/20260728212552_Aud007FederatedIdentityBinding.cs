using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Aud007FederatedIdentityBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalObjectId",
                table: "IdentityAccounts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalTenantId",
                table: "IdentityAccounts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityAccounts_ExternalTenantId_ExternalObjectId",
                table: "IdentityAccounts",
                columns: new[] { "ExternalTenantId", "ExternalObjectId" },
                unique: true,
                filter: "\"ExternalObjectId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IdentityAccounts_ExternalTenantId_ExternalObjectId",
                table: "IdentityAccounts");

            migrationBuilder.DropColumn(
                name: "ExternalObjectId",
                table: "IdentityAccounts");

            migrationBuilder.DropColumn(
                name: "ExternalTenantId",
                table: "IdentityAccounts");
        }
    }
}
