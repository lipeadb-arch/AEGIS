using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Auditable_Posture_Snapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PostureSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    SchemaVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    FormulaVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CatalogVersion = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SemanticFamily = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SourceType = table.Column<int>(type: "integer", nullable: true),
                    SourceLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CapturedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Score = table.Column<double>(type: "double precision", nullable: true),
                    AchievedPoints = table.Column<int>(type: "integer", nullable: false),
                    PossiblePoints = table.Column<int>(type: "integer", nullable: false),
                    EligiblePoints = table.Column<int>(type: "integer", nullable: false),
                    Coverage = table.Column<double>(type: "double precision", nullable: false),
                    EvaluatedItems = table.Column<int>(type: "integer", nullable: false),
                    EligibleItems = table.Column<int>(type: "integer", nullable: false),
                    CompliantCount = table.Column<int>(type: "integer", nullable: false),
                    NonCompliantCount = table.Column<int>(type: "integer", nullable: false),
                    MitigatedCount = table.Column<int>(type: "integer", nullable: false),
                    NotEvaluatedCount = table.Column<int>(type: "integer", nullable: false),
                    ErrorCount = table.Column<int>(type: "integer", nullable: false),
                    NotApplicableCount = table.Column<int>(type: "integer", nullable: false),
                    DataRecency = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostureSnapshots", x => x.Id);
                    table.UniqueConstraint("AK_PostureSnapshots_Id_TenantId", x => new { x.Id, x.TenantId });
                });

            migrationBuilder.CreateTable(
                name: "PostureSnapshotControls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubcategoryCode = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    FunctionCode = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    Evaluated = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: true),
                    AchievedPoints = table.Column<int>(type: "integer", nullable: false),
                    MaxPoints = table.Column<int>(type: "integer", nullable: false),
                    VerdictSource = table.Column<int>(type: "integer", nullable: true),
                    EvaluatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EvidenceRefs = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostureSnapshotControls", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PostureSnapshotControls_PostureSnapshots_SnapshotId_TenantId",
                        columns: x => new { x.SnapshotId, x.TenantId },
                        principalTable: "PostureSnapshots",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PostureSnapshotIndicators",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    IndicatorId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Evidence = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    AffectedObjectCount = table.Column<int>(type: "integer", nullable: false),
                    NistCodes = table.Column<string>(type: "jsonb", nullable: false),
                    MitreTechniques = table.Column<string>(type: "jsonb", nullable: false),
                    SourceType = table.Column<int>(type: "integer", nullable: false),
                    CollectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostureSnapshotIndicators", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PostureSnapshotIndicators_PostureSnapshots_SnapshotId_Tenan~",
                        columns: x => new { x.SnapshotId, x.TenantId },
                        principalTable: "PostureSnapshots",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PostureSnapshotControls_SnapshotId_TenantId",
                table: "PostureSnapshotControls",
                columns: new[] { "SnapshotId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_PostureSnapshotControls_TenantId_SnapshotId",
                table: "PostureSnapshotControls",
                columns: new[] { "TenantId", "SnapshotId" });

            migrationBuilder.CreateIndex(
                name: "IX_PostureSnapshotIndicators_SnapshotId_TenantId",
                table: "PostureSnapshotIndicators",
                columns: new[] { "SnapshotId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_PostureSnapshotIndicators_TenantId_SnapshotId",
                table: "PostureSnapshotIndicators",
                columns: new[] { "TenantId", "SnapshotId" });

            migrationBuilder.CreateIndex(
                name: "IX_PostureSnapshots_TenantId_CapturedAt",
                table: "PostureSnapshots",
                columns: new[] { "TenantId", "CapturedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PostureSnapshots_TenantId_Type_CapturedAt",
                table: "PostureSnapshots",
                columns: new[] { "TenantId", "Type", "CapturedAt" });

            // [AEGIS-AUD-036] Imutabilidade REFORÇADA no banco: a fotografia publicada é APPEND-ONLY. Uma função
            // de gatilho recusa fisicamente UPDATE e DELETE em qualquer linha das três tabelas — nem um endpoint
            // operacional, nem um bug de serviço, nem um UPDATE manual conseguem alterar ou apagar uma fotografia
            // já publicada. O INSERT (publicação) é permitido. Não depende de esconder botões no frontend nem só
            // da ausência de update/delete no serviço: é o próprio banco que garante que a foto não mude de
            // significado depois. (SQLite/EnsureCreated dos testes não recebe o gatilho — lá a garantia é o
            // serviço; o gatilho é verificado no gate PostgreSQL real.)
            migrationBuilder.Sql(@"
CREATE OR REPLACE FUNCTION aegis_block_posture_snapshot_mutation() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'PostureSnapshot e append-only: % em % nao e permitido (fotografia auditavel imutavel).',
        TG_OP, TG_TABLE_NAME;
END;
$$ LANGUAGE plpgsql;");

            migrationBuilder.Sql(@"
CREATE TRIGGER trg_posture_snapshots_immutable
    BEFORE UPDATE OR DELETE ON ""PostureSnapshots""
    FOR EACH ROW EXECUTE FUNCTION aegis_block_posture_snapshot_mutation();");

            migrationBuilder.Sql(@"
CREATE TRIGGER trg_posture_snapshot_controls_immutable
    BEFORE UPDATE OR DELETE ON ""PostureSnapshotControls""
    FOR EACH ROW EXECUTE FUNCTION aegis_block_posture_snapshot_mutation();");

            migrationBuilder.Sql(@"
CREATE TRIGGER trg_posture_snapshot_indicators_immutable
    BEFORE UPDATE OR DELETE ON ""PostureSnapshotIndicators""
    FOR EACH ROW EXECUTE FUNCTION aegis_block_posture_snapshot_mutation();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove SÓ as estruturas novas — gatilhos, função e tabelas desta migration. Não toca dado preexistente.
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS trg_posture_snapshot_indicators_immutable ON ""PostureSnapshotIndicators"";");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS trg_posture_snapshot_controls_immutable ON ""PostureSnapshotControls"";");
            migrationBuilder.Sql(@"DROP TRIGGER IF EXISTS trg_posture_snapshots_immutable ON ""PostureSnapshots"";");
            migrationBuilder.Sql(@"DROP FUNCTION IF EXISTS aegis_block_posture_snapshot_mutation();");

            migrationBuilder.DropTable(
                name: "PostureSnapshotControls");

            migrationBuilder.DropTable(
                name: "PostureSnapshotIndicators");

            migrationBuilder.DropTable(
                name: "PostureSnapshots");
        }
    }
}
