using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PostureFoundation_ProvenanceAndEvidenceType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EvidenceType",
                table: "AssessmentRules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ReferenceDatasetProvenances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FrameworkVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SupersededAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Identifier = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Classification = table.Column<int>(type: "integer", nullable: false),
                    SchemaVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Origin = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    OfficialReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Release = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    OfficialUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ObtainedOn = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    AppliesToCatalog = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MethodologyVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferenceDatasetProvenances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReferenceDatasetProvenances_FrameworkVersions_FrameworkVers~",
                        column: x => x.FrameworkVersionId,
                        principalTable: "FrameworkVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReferenceDatasetProvenances_FrameworkVersionId_Kind_Revision",
                table: "ReferenceDatasetProvenances",
                columns: new[] { "FrameworkVersionId", "Kind", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ReferenceDatasetProvenance_Current",
                table: "ReferenceDatasetProvenances",
                columns: new[] { "FrameworkVersionId", "Kind" },
                unique: true,
                filter: "\"IsCurrent\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReferenceDatasetProvenances");

            migrationBuilder.DropColumn(
                name: "EvidenceType",
                table: "AssessmentRules");
        }
    }
}
