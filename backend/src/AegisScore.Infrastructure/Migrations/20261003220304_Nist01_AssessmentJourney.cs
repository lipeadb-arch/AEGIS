using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Nist01_AssessmentJourney : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Evaluations_AssessmentScopeId",
                table: "Evaluations");

            migrationBuilder.AlterColumn<Guid>(
                name: "BusinessUnitId",
                table: "Scopes",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "BusinessProcessId",
                table: "Scopes",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Scopes",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "Scopes",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Notes",
                table: "Evidence",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OriginKind",
                table: "Evidence",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "OriginLabel",
                table: "Evidence",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginRef",
                table: "Evidence",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginScope",
                table: "Evidence",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RecordedByAccountId",
                table: "Evidence",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecordedByName",
                table: "Evidence",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RemovedAt",
                table: "Evidence",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemovedByName",
                table: "Evidence",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "Evidence",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Gaps",
                table: "Evaluations",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImprovementGuidance",
                table: "Evaluations",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NotApplicable",
                table: "Evaluations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "OwnerName",
                table: "Evaluations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewedByName",
                table: "Evaluations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RiskImpact",
                table: "Evaluations",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Evaluations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Evaluations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Assessments",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MethodologyVersion",
                table: "Assessments",
                type: "character varying(60)",
                maxLength: 60,
                nullable: false,
                defaultValue: "aegis-methodology-v1");

            // [AEGIS-NIST-JOURNEY-01] A avaliação passa a ter TenantId próprio: herda o do escopo (a FK garante que todo
            // escopo existe). Em seguida, duas checagens que ABORTAM a migration — a transação desfaz tudo e nenhum dado é
            // alterado — em vez de apagar ou escolher arbitrariamente: avaliação duplicada por (escopo, subcategoria), que o
            // índice único recusaria, e avaliação que ficou sem tenant.
            migrationBuilder.Sql(
                """
                UPDATE "Evaluations" AS e SET "TenantId" = s."TenantId"
                FROM "Scopes" AS s
                WHERE s."Id" = e."AssessmentScopeId";

                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Evaluations" GROUP BY "AssessmentScopeId", "SubcategoryId" HAVING COUNT(*) > 1) THEN
                        RAISE EXCEPTION 'AEGIS-NIST-JOURNEY-01: ha avaliacoes duplicadas para o mesmo escopo e subcategoria; resolva-as antes de aplicar esta migration (nada foi alterado).';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "Evaluations" WHERE "TenantId" = '00000000-0000-0000-0000-000000000000') THEN
                        RAISE EXCEPTION 'AEGIS-NIST-JOURNEY-01: ha avaliacoes sem tenant resolvido pelo escopo (nada foi alterado).';
                    END IF;
                END $$;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Evidence_TenantId_AssessmentScopeId_SubcategoryCode",
                table: "Evidence",
                columns: new[] { "TenantId", "AssessmentScopeId", "SubcategoryCode" });

            migrationBuilder.CreateIndex(
                name: "IX_Evaluations_TenantId",
                table: "Evaluations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "UX_Evaluations_ScopeSubcategory",
                table: "Evaluations",
                columns: new[] { "AssessmentScopeId", "SubcategoryId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Evidence_TenantId_AssessmentScopeId_SubcategoryCode",
                table: "Evidence");

            migrationBuilder.DropIndex(
                name: "IX_Evaluations_TenantId",
                table: "Evaluations");

            migrationBuilder.DropIndex(
                name: "UX_Evaluations_ScopeSubcategory",
                table: "Evaluations");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "Scopes");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "Scopes");

            migrationBuilder.DropColumn(
                name: "Notes",
                table: "Evidence");

            migrationBuilder.DropColumn(
                name: "OriginKind",
                table: "Evidence");

            migrationBuilder.DropColumn(
                name: "OriginLabel",
                table: "Evidence");

            migrationBuilder.DropColumn(
                name: "OriginRef",
                table: "Evidence");

            migrationBuilder.DropColumn(
                name: "OriginScope",
                table: "Evidence");

            migrationBuilder.DropColumn(
                name: "RecordedByAccountId",
                table: "Evidence");

            migrationBuilder.DropColumn(
                name: "RecordedByName",
                table: "Evidence");

            migrationBuilder.DropColumn(
                name: "RemovedAt",
                table: "Evidence");

            migrationBuilder.DropColumn(
                name: "RemovedByName",
                table: "Evidence");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "Evidence");

            migrationBuilder.DropColumn(
                name: "Gaps",
                table: "Evaluations");

            migrationBuilder.DropColumn(
                name: "ImprovementGuidance",
                table: "Evaluations");

            migrationBuilder.DropColumn(
                name: "NotApplicable",
                table: "Evaluations");

            migrationBuilder.DropColumn(
                name: "OwnerName",
                table: "Evaluations");

            migrationBuilder.DropColumn(
                name: "ReviewedByName",
                table: "Evaluations");

            migrationBuilder.DropColumn(
                name: "RiskImpact",
                table: "Evaluations");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Evaluations");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Evaluations");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "Assessments");

            migrationBuilder.DropColumn(
                name: "MethodologyVersion",
                table: "Assessments");

            migrationBuilder.AlterColumn<Guid>(
                name: "BusinessUnitId",
                table: "Scopes",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "BusinessProcessId",
                table: "Scopes",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Evaluations_AssessmentScopeId",
                table: "Evaluations",
                column: "AssessmentScopeId");
        }
    }
}
