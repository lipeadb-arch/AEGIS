using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Closure01_KnightManualAssessments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KnightManualAssessments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReferenceKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Result = table.Column<int>(type: "integer", nullable: false),
                    Justification = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ResponsibleName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EvidenceReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    EvidenceDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    EvidenceDocumentTitle = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    EvidenceDocumentSha256 = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ValidUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    ReferenceDisposition = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CatalogVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    RecordedByAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    RecordedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnightManualAssessments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KnightManualAssessments_TenantId_ReferenceKey_RecordedAt",
                table: "KnightManualAssessments",
                columns: new[] { "TenantId", "ReferenceKey", "RecordedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KnightManualAssessments");
        }
    }
}
