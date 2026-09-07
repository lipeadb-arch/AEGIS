using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Product03_RemediationAndReport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ActionPlans_Risks_RiskId",
                table: "ActionPlans");

            migrationBuilder.AddColumn<string>(
                name: "ClientName",
                table: "PostureSnapshots",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CollectionLimitations",
                table: "PostureSnapshots",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<Guid>(
                name: "SourceRunId",
                table: "PostureSnapshots",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "RiskId",
                table: "ActionPlans",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExecutedAt",
                table: "ActionPlans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExecutionEvidenceRef",
                table: "ActionPlans",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExecutionNotes",
                table: "ActionPlans",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KnightIndicatorId",
                table: "ActionPlans",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OriginAffectedCount",
                table: "ActionPlans",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginRunId",
                table: "ActionPlans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "ActionPlans",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "ActionPlans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_ActionPlans_Id_TenantId",
                table: "ActionPlans",
                columns: new[] { "Id", "TenantId" });

            migrationBuilder.CreateTable(
                name: "ActionPlanEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActionPlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FromStatus = table.Column<int>(type: "integer", nullable: true),
                    ToStatus = table.Column<int>(type: "integer", nullable: true),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionPlanEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActionPlanEvents_ActionPlans_ActionPlanId_TenantId",
                        columns: x => new { x.ActionPlanId, x.TenantId },
                        principalTable: "ActionPlans",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ActionPlanValidations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActionPlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    IndicatorId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Method = table.Column<int>(type: "integer", nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    ValidationRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    EvidenceReference = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ObservedBefore = table.Column<int>(type: "integer", nullable: true),
                    ObservedAfter = table.Column<int>(type: "integer", nullable: true),
                    ObjectsNoLongerPresent = table.Column<int>(type: "integer", nullable: true),
                    ComparedBySets = table.Column<bool>(type: "boolean", nullable: false),
                    Rationale = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedByAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionPlanValidations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActionPlanValidations_ActionPlans_ActionPlanId_TenantId",
                        columns: x => new { x.ActionPlanId, x.TenantId },
                        principalTable: "ActionPlans",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PostureSnapshotActionItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActionPlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    IndicatorId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ProposedAction = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ResponsiblePerson = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ResponsibleArea = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    WasOverdue = table.Column<bool>(type: "boolean", nullable: false),
                    NextStep = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ValidationMethod = table.Column<int>(type: "integer", nullable: true),
                    ValidationOutcome = table.Column<int>(type: "integer", nullable: true),
                    ValidatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ObservedBefore = table.Column<int>(type: "integer", nullable: true),
                    ObservedAfter = table.Column<int>(type: "integer", nullable: true),
                    ComparedBySets = table.Column<bool>(type: "boolean", nullable: false),
                    ValidationRationale = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PostureSnapshotActionItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PostureSnapshotActionItems_PostureSnapshots_SnapshotId_Tena~",
                        columns: x => new { x.SnapshotId, x.TenantId },
                        principalTable: "PostureSnapshots",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActionPlans_TenantId_KnightIndicatorId_Status",
                table: "ActionPlans",
                columns: new[] { "TenantId", "KnightIndicatorId", "Status" });

            migrationBuilder.CreateIndex(
                name: "UX_ActionPlans_ActiveByFinding",
                table: "ActionPlans",
                columns: new[] { "TenantId", "KnightIndicatorId" },
                unique: true,
                filter: "\"KnightIndicatorId\" IS NOT NULL AND \"Status\" IN (0, 1, 4)");

            migrationBuilder.CreateIndex(
                name: "IX_ActionPlanEvents_ActionPlanId_TenantId",
                table: "ActionPlanEvents",
                columns: new[] { "ActionPlanId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_ActionPlanEvents_TenantId_ActionPlanId_At",
                table: "ActionPlanEvents",
                columns: new[] { "TenantId", "ActionPlanId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_ActionPlanValidations_ActionPlanId_TenantId",
                table: "ActionPlanValidations",
                columns: new[] { "ActionPlanId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_ActionPlanValidations_TenantId_ActionPlanId_DecidedAt",
                table: "ActionPlanValidations",
                columns: new[] { "TenantId", "ActionPlanId", "DecidedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PostureSnapshotActionItems_SnapshotId_TenantId",
                table: "PostureSnapshotActionItems",
                columns: new[] { "SnapshotId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_PostureSnapshotActionItems_TenantId_SnapshotId",
                table: "PostureSnapshotActionItems",
                columns: new[] { "TenantId", "SnapshotId" });

            migrationBuilder.AddForeignKey(
                name: "FK_ActionPlans_Risks_RiskId",
                table: "ActionPlans",
                column: "RiskId",
                principalTable: "Risks",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ActionPlans_Risks_RiskId",
                table: "ActionPlans");

            migrationBuilder.DropTable(
                name: "ActionPlanEvents");

            migrationBuilder.DropTable(
                name: "ActionPlanValidations");

            migrationBuilder.DropTable(
                name: "PostureSnapshotActionItems");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_ActionPlans_Id_TenantId",
                table: "ActionPlans");

            migrationBuilder.DropIndex(
                name: "IX_ActionPlans_TenantId_KnightIndicatorId_Status",
                table: "ActionPlans");

            migrationBuilder.DropIndex(
                name: "UX_ActionPlans_ActiveByFinding",
                table: "ActionPlans");

            migrationBuilder.DropColumn(
                name: "ClientName",
                table: "PostureSnapshots");

            migrationBuilder.DropColumn(
                name: "CollectionLimitations",
                table: "PostureSnapshots");

            migrationBuilder.DropColumn(
                name: "SourceRunId",
                table: "PostureSnapshots");

            migrationBuilder.DropColumn(
                name: "ExecutedAt",
                table: "ActionPlans");

            migrationBuilder.DropColumn(
                name: "ExecutionEvidenceRef",
                table: "ActionPlans");

            migrationBuilder.DropColumn(
                name: "ExecutionNotes",
                table: "ActionPlans");

            migrationBuilder.DropColumn(
                name: "KnightIndicatorId",
                table: "ActionPlans");

            migrationBuilder.DropColumn(
                name: "OriginAffectedCount",
                table: "ActionPlans");

            migrationBuilder.DropColumn(
                name: "OriginRunId",
                table: "ActionPlans");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "ActionPlans");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "ActionPlans");

            migrationBuilder.AlterColumn<Guid>(
                name: "RiskId",
                table: "ActionPlans",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ActionPlans_Risks_RiskId",
                table: "ActionPlans",
                column: "RiskId",
                principalTable: "Risks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
