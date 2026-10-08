using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AuditorContext01_Conversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditorConversationTurns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Question = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Reply = table.Column<string>(type: "character varying(12000)", maxLength: 12000, nullable: false),
                    FocusLabel = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Simulated = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditorConversationTurns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditorConversationTurns_TenantId_AccountId_ConversationId",
                table: "AuditorConversationTurns",
                columns: new[] { "TenantId", "AccountId", "ConversationId" });

            migrationBuilder.CreateIndex(
                name: "UX_AuditorConversationTurns_Sequence",
                table: "AuditorConversationTurns",
                columns: new[] { "TenantId", "ConversationId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditorConversationTurns");
        }
    }
}
