using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Nist02_CompleteJourney : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Evaluations_ScopeSubcategory",
                table: "Evaluations");

            migrationBuilder.AddColumn<double>(
                name: "MaturityCurrent",
                table: "PostureSnapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MaturityGap",
                table: "PostureSnapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MaturityTarget",
                table: "PostureSnapshots",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "NistAssessmentId",
                table: "PostureSnapshots",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "NistCycleId",
                table: "PostureSnapshots",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NistCycleName",
                table: "PostureSnapshots",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NistReportJson",
                table: "PostureSnapshots",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "NistScopeId",
                table: "PostureSnapshots",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CycleId",
                table: "Evidence",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssessorName",
                table: "Evaluations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AssessorUserId",
                table: "Evaluations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ContentOrigin",
                table: "Evaluations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "CycleId",
                table: "Evaluations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "OriginNote",
                table: "Evaluations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerContact",
                table: "Evaluations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OwnerIsExternal",
                table: "Evaluations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                table: "Evaluations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReviewDecision",
                table: "Evaluations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReviewDecisionAt",
                table: "Evaluations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReviewDecisionByAccountId",
                table: "Evaluations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewDecisionByName",
                table: "Evaluations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewDecisionFingerprint",
                table: "Evaluations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewDecisionNote",
                table: "Evaluations",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewerName",
                table: "Evaluations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReviewerUserId",
                table: "Evaluations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceEvaluationId",
                table: "Evaluations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginNistAssessmentId",
                table: "ActionPlans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginNistCycleId",
                table: "ActionPlans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginNistFindingId",
                table: "ActionPlans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginNistScopeId",
                table: "ActionPlans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OriginSubcategoryCode",
                table: "ActionPlans",
                type: "character varying(15)",
                maxLength: 15,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponsibleContact",
                table: "ActionPlans",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ResponsibleIsExternal",
                table: "ActionPlans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ResponsibleUserId",
                table: "ActionPlans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ChangesJson",
                table: "ActionPlanEvents",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "NistAuditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CycleId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssessmentScopeId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubcategoryCode = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: true),
                    Subject = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Summary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ChangesJson = table.Column<string>(type: "text", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NistAuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NistCycles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    PeriodKind = table.Column<int>(type: "integer", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    SeedFromCycleId = table.Column<Guid>(type: "uuid", nullable: true),
                    SeedMode = table.Column<int>(type: "integer", nullable: false),
                    CreatedByAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClosedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NistCycles", x => x.Id);
                    table.UniqueConstraint("AK_NistCycles_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.ForeignKey(
                        name: "FK_NistCycles_Assessments_AssessmentId",
                        column: x => x.AssessmentId,
                        principalTable: "Assessments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NistFindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CycleId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssessmentScopeId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubcategoryCode = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Condition = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Risk = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Impact = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    SeverityRationale = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    PriorityRationale = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Recommendation = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    EvidenceIds = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StatusNote = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    StatusChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StatusChangedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    OriginContextJson = table.Column<string>(type: "text", nullable: false),
                    CreatedByAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NistFindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NistFindings_NistCycles_CycleId_TenantId",
                        columns: x => new { x.CycleId, x.TenantId },
                        principalTable: "NistCycles",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NistProcedures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssessmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CycleId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssessmentScopeId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubcategoryCode = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    Method = table.Column<int>(type: "integer", nullable: false),
                    Procedure = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: true),
                    Observation = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    PerformedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ResultRecordedByAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResultRecordedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ResultRecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EvidenceIds = table.Column<string>(type: "jsonb", nullable: false),
                    ContentOrigin = table.Column<int>(type: "integer", nullable: false),
                    OriginNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SourceProcedureId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RemovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RemovedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NistProcedures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NistProcedures_NistCycles_CycleId_TenantId",
                        columns: x => new { x.CycleId, x.TenantId },
                        principalTable: "NistCycles",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            // [AEGIS-NIST-JOURNEY-02] Reconciliação dos dados do #87, ANTES dos índices e da FK da rodada:
            //   • cada avaliação existente ganha EXATAMENTE uma rodada inicial ("Rodada inicial", período informado), com o
            //     tenant da própria avaliação;
            //   • toda avaliação de subcategoria e toda evidência NIST passam a apontar para a rodada inicial da avaliação do
            //     seu escopo — uma por avaliação, então não há escolha arbitrária; nada é apagado nem fundido;
            //   • se sobrar avaliação sem rodada ou com tenant divergente, a migration ABORTA sem alterar nada (transação).
            migrationBuilder.Sql("""
                INSERT INTO "NistCycles" ("Id", "TenantId", "AssessmentId", "Name", "PeriodKind", "PeriodStart", "PeriodEnd",
                    "Status", "SeedFromCycleId", "SeedMode", "CreatedByAccountId", "CreatedByName", "ClosedAt", "ClosedByName",
                    "Version", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), a."TenantId", a."Id", 'Rodada inicial', 2,
                       COALESCE(a."StartDate", (a."CreatedAt" AT TIME ZONE 'UTC')::date),
                       GREATEST(COALESCE(a."EndDate", a."StartDate", (a."CreatedAt" AT TIME ZONE 'UTC')::date),
                                COALESCE(a."StartDate", (a."CreatedAt" AT TIME ZONE 'UTC')::date)),
                       0, NULL, 0, NULL, 'Migração AEGIS-NIST-JOURNEY-02', NULL, NULL, 1, now(), NULL
                FROM "Assessments" a;

                UPDATE "Evaluations" e
                SET "CycleId" = c."Id"
                FROM "Scopes" s
                JOIN "NistCycles" c ON c."AssessmentId" = s."AssessmentId"
                WHERE e."AssessmentScopeId" = s."Id";

                UPDATE "Evidence" ev
                SET "CycleId" = c."Id"
                FROM "Scopes" s
                JOIN "NistCycles" c ON c."AssessmentId" = s."AssessmentId"
                WHERE ev."AssessmentScopeId" = s."Id";

                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Evaluations" WHERE "CycleId" = '00000000-0000-0000-0000-000000000000') THEN
                        RAISE EXCEPTION 'AEGIS-NIST-JOURNEY-02: ha avaliacoes de subcategoria sem rodada resolvida pelo escopo (nada foi alterado).';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "Evaluations" e JOIN "NistCycles" c ON c."Id" = e."CycleId" WHERE e."TenantId" <> c."TenantId") THEN
                        RAISE EXCEPTION 'AEGIS-NIST-JOURNEY-02: ha avaliacoes cujo tenant diverge do tenant da avaliacao (nada foi alterado).';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "Evidence" WHERE "AssessmentScopeId" IS NOT NULL AND "CycleId" IS NULL) THEN
                        RAISE EXCEPTION 'AEGIS-NIST-JOURNEY-02: ha evidencias de avaliacao sem rodada resolvida pelo escopo (nada foi alterado).';
                    END IF;
                END $$;
                """);

            // Trilha NIST append-only: o banco recusa UPDATE e DELETE (o INSERT é o único caminho).
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION aegis_block_nist_audit_mutation() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'NistAuditEntries e append-only: % nao e permitido (trilha imutavel).', TG_OP;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER trg_nist_audit_entries_immutable
                    BEFORE UPDATE OR DELETE ON "NistAuditEntries"
                    FOR EACH ROW EXECUTE FUNCTION aegis_block_nist_audit_mutation();
                """);

            migrationBuilder.CreateIndex(
                name: "IX_PostureSnapshots_TenantId_NistAssessmentId_NistCycleId_Nist~",
                table: "PostureSnapshots",
                columns: new[] { "TenantId", "NistAssessmentId", "NistCycleId", "NistScopeId" });

            migrationBuilder.CreateIndex(
                name: "IX_Evidence_TenantId_CycleId_AssessmentScopeId_SubcategoryCode",
                table: "Evidence",
                columns: new[] { "TenantId", "CycleId", "AssessmentScopeId", "SubcategoryCode" });

            migrationBuilder.CreateIndex(
                name: "IX_Evaluations_CycleId_TenantId",
                table: "Evaluations",
                columns: new[] { "CycleId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_Evaluations_TenantId_CycleId",
                table: "Evaluations",
                columns: new[] { "TenantId", "CycleId" });

            migrationBuilder.CreateIndex(
                name: "UX_Evaluations_ScopeCycleSubcategory",
                table: "Evaluations",
                columns: new[] { "AssessmentScopeId", "CycleId", "SubcategoryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActionPlans_TenantId_OriginNistAssessmentId_OriginNistCycle~",
                table: "ActionPlans",
                columns: new[] { "TenantId", "OriginNistAssessmentId", "OriginNistCycleId" });

            migrationBuilder.CreateIndex(
                name: "UX_ActionPlans_ActiveByNistFinding",
                table: "ActionPlans",
                columns: new[] { "TenantId", "OriginNistFindingId" },
                unique: true,
                filter: "\"OriginKind\" = 3 AND \"Status\" IN (0, 1, 4)");

            migrationBuilder.CreateIndex(
                name: "IX_NistAuditEntries_TenantId_AssessmentId_At",
                table: "NistAuditEntries",
                columns: new[] { "TenantId", "AssessmentId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_NistAuditEntries_TenantId_CycleId_AssessmentScopeId_Subcate~",
                table: "NistAuditEntries",
                columns: new[] { "TenantId", "CycleId", "AssessmentScopeId", "SubcategoryCode" });

            migrationBuilder.CreateIndex(
                name: "IX_NistCycles_TenantId_AssessmentId",
                table: "NistCycles",
                columns: new[] { "TenantId", "AssessmentId" });

            migrationBuilder.CreateIndex(
                name: "UX_NistCycles_AssessmentName",
                table: "NistCycles",
                columns: new[] { "AssessmentId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NistFindings_CycleId_TenantId",
                table: "NistFindings",
                columns: new[] { "CycleId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_NistFindings_TenantId_AssessmentId_Status",
                table: "NistFindings",
                columns: new[] { "TenantId", "AssessmentId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_NistFindings_TenantId_CycleId_AssessmentScopeId_Subcategory~",
                table: "NistFindings",
                columns: new[] { "TenantId", "CycleId", "AssessmentScopeId", "SubcategoryCode" });

            migrationBuilder.CreateIndex(
                name: "IX_NistProcedures_CycleId_TenantId",
                table: "NistProcedures",
                columns: new[] { "CycleId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_NistProcedures_TenantId_CycleId_AssessmentScopeId_Subcatego~",
                table: "NistProcedures",
                columns: new[] { "TenantId", "CycleId", "AssessmentScopeId", "SubcategoryCode" });

            migrationBuilder.AddForeignKey(
                name: "FK_Evaluations_NistCycles_CycleId_TenantId",
                table: "Evaluations",
                columns: new[] { "CycleId", "TenantId" },
                principalTable: "NistCycles",
                principalColumns: new[] { "Id", "TenantId" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_nist_audit_entries_immutable ON "NistAuditEntries";
                DROP FUNCTION IF EXISTS aegis_block_nist_audit_mutation();
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Evaluations_NistCycles_CycleId_TenantId",
                table: "Evaluations");

            migrationBuilder.DropTable(
                name: "NistAuditEntries");

            migrationBuilder.DropTable(
                name: "NistFindings");

            migrationBuilder.DropTable(
                name: "NistProcedures");

            migrationBuilder.DropTable(
                name: "NistCycles");

            migrationBuilder.DropIndex(
                name: "IX_PostureSnapshots_TenantId_NistAssessmentId_NistCycleId_Nist~",
                table: "PostureSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_Evidence_TenantId_CycleId_AssessmentScopeId_SubcategoryCode",
                table: "Evidence");

            migrationBuilder.DropIndex(
                name: "IX_Evaluations_CycleId_TenantId",
                table: "Evaluations");

            migrationBuilder.DropIndex(
                name: "IX_Evaluations_TenantId_CycleId",
                table: "Evaluations");

            migrationBuilder.DropIndex(
                name: "UX_Evaluations_ScopeCycleSubcategory",
                table: "Evaluations");

            migrationBuilder.DropIndex(
                name: "IX_ActionPlans_TenantId_OriginNistAssessmentId_OriginNistCycle~",
                table: "ActionPlans");

            migrationBuilder.DropIndex(
                name: "UX_ActionPlans_ActiveByNistFinding",
                table: "ActionPlans");

            migrationBuilder.DropColumn(
                name: "MaturityCurrent",
                table: "PostureSnapshots");

            migrationBuilder.DropColumn(
                name: "MaturityGap",
                table: "PostureSnapshots");

            migrationBuilder.DropColumn(
                name: "MaturityTarget",
                table: "PostureSnapshots");

            migrationBuilder.DropColumn(
                name: "NistAssessmentId",
                table: "PostureSnapshots");

            migrationBuilder.DropColumn(
                name: "NistCycleId",
                table: "PostureSnapshots");

            migrationBuilder.DropColumn(
                name: "NistCycleName",
                table: "PostureSnapshots");

            migrationBuilder.DropColumn(
                name: "NistReportJson",
                table: "PostureSnapshots");

            migrationBuilder.DropColumn(
                name: "NistScopeId",
                table: "PostureSnapshots");

            migrationBuilder.DropColumn(
                name: "CycleId",
                table: "Evidence");

            migrationBuilder.DropColumn(
                name: "AssessorName",
                table: "Evaluations");

            migrationBuilder.DropColumn(
                name: "AssessorUserId",
                table: "Evaluations");

            migrationBuilder.DropColumn(
                name: "ContentOrigin",
                table: "Evaluations");

            migrationBuilder.DropColumn(
                name: "CycleId",
                table: "Evaluations");

            migrationBuilder.DropColumn(
                name: "OriginNote",
                table: "Evaluations");

            migrationBuilder.DropColumn(
                name: "OwnerContact",
                table: "Evaluations");

            migrationBuilder.DropColumn(
                name: "OwnerIsExternal",
                table: "Evaluations");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "Evaluations");

            migrationBuilder.DropColumn(
                name: "ReviewDecision",
                table: "Evaluations");

            migrationBuilder.DropColumn(
                name: "ReviewDecisionAt",
                table: "Evaluations");

            migrationBuilder.DropColumn(
                name: "ReviewDecisionByAccountId",
                table: "Evaluations");

            migrationBuilder.DropColumn(
                name: "ReviewDecisionByName",
                table: "Evaluations");

            migrationBuilder.DropColumn(
                name: "ReviewDecisionFingerprint",
                table: "Evaluations");

            migrationBuilder.DropColumn(
                name: "ReviewDecisionNote",
                table: "Evaluations");

            migrationBuilder.DropColumn(
                name: "ReviewerName",
                table: "Evaluations");

            migrationBuilder.DropColumn(
                name: "ReviewerUserId",
                table: "Evaluations");

            migrationBuilder.DropColumn(
                name: "SourceEvaluationId",
                table: "Evaluations");

            migrationBuilder.DropColumn(
                name: "OriginNistAssessmentId",
                table: "ActionPlans");

            migrationBuilder.DropColumn(
                name: "OriginNistCycleId",
                table: "ActionPlans");

            migrationBuilder.DropColumn(
                name: "OriginNistFindingId",
                table: "ActionPlans");

            migrationBuilder.DropColumn(
                name: "OriginNistScopeId",
                table: "ActionPlans");

            migrationBuilder.DropColumn(
                name: "OriginSubcategoryCode",
                table: "ActionPlans");

            migrationBuilder.DropColumn(
                name: "ResponsibleContact",
                table: "ActionPlans");

            migrationBuilder.DropColumn(
                name: "ResponsibleIsExternal",
                table: "ActionPlans");

            migrationBuilder.DropColumn(
                name: "ResponsibleUserId",
                table: "ActionPlans");

            migrationBuilder.DropColumn(
                name: "ChangesJson",
                table: "ActionPlanEvents");

            migrationBuilder.CreateIndex(
                name: "UX_Evaluations_ScopeSubcategory",
                table: "Evaluations",
                columns: new[] { "AssessmentScopeId", "SubcategoryId" },
                unique: true);
        }
    }
}
