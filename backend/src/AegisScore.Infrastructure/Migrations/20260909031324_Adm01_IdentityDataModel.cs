using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AegisScore.Infrastructure.Migrations
{
    /// <summary>
    /// [AEGIS-ADM-01] Primeiro recorte do AEGIS Data Model por IDENTIDADE. ESTRITAMENTE ADITIVA — nenhuma
    /// coluna existente muda de tipo ou de nulidade, nenhuma migration anterior e reescrita e nenhum dado
    /// antigo e reescrito, retropreenchido ou apagado:
    ///   • 5 tabelas novas, todas tenant-owned: a AQUISICAO identificavel da coleta, a ENTIDADE canonica de
    ///     identidade, o VINCULO com a origem, a OBSERVACAO do objeto naquela aquisicao e a COMPLETUDE por
    ///     conjunto observado;
    ///   • FKs COMPOSTAS tenant-safe: aquisicao → Connectors (Id, TenantId); observacao e estado de conjunto →
    ///     IdentityAcquisitions (Id, TenantId); observacao e vinculo → IdentityEntities (Id, TenantId). O
    ///     proprio banco recusa filho de tenant divergente, e a evidencia nao existe sem a aquisicao (Cascade);
    ///   • 3 indices UNICOS que sao as invariantes de identidade do pacote:
    ///       UX_IdentitySourceLink_Natural         (TenantId, DirectoryNamespace, ExternalId) — o mesmo objeto
    ///         em dois conjuntos resolve para UMA entidade, e o mesmo identificador em OUTRO diretorio resolve
    ///         para uma entidade DIFERENTE; nome e UPN nao participam da chave;
    ///       UX_IdentityEntityObservation_Natural  (TenantId, AcquisitionId, IdentityEntityId, Set) — o retry
    ///         da mesma aquisicao e idempotente no BANCO, nao apenas no codigo;
    ///       UX_IdentityObservationSetState_Natural(TenantId, AcquisitionId, Set) — um estado por conjunto.
    ///   • 1 coluna ANULAVEL IdentityAcquisitionId em KnightAssessmentRuns: a procedencia da evidencia de uma
    ///     avaliacao. Execucoes ja gravadas permanecem NULL — uma avaliacao anterior a este pacote nao sabe de
    ///     qual coleta nasceu, e dizer isso e melhor do que carimba-la com a coleta de hoje. Deliberadamente
    ///     SEM chave estrangeira: a evidencia cascateia com o conector, e uma FK obrigaria a escolher entre
    ///     travar a remocao do conector e destruir o historico de avaliacoes.
    /// </summary>
    public partial class Adm01_IdentityDataModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "IdentityAcquisitionId",
                table: "KnightAssessmentRuns",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "IdentityAcquisitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectorConfigId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<int>(type: "integer", nullable: false),
                    DirectoryNamespace = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SourceLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SchemaVersion = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    NormalizationVersion = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    AcquiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    FactsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CapabilitiesJson = table.Column<string>(type: "jsonb", nullable: false),
                    ContentFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityAcquisitions", x => x.Id);
                    table.UniqueConstraint("AK_IdentityAcquisitions_Id_TenantId", x => new { x.Id, x.TenantId });
                    table.ForeignKey(
                        name: "FK_IdentityAcquisitions_Connectors_ConnectorConfigId_TenantId",
                        columns: x => new { x.ConnectorConfigId, x.TenantId },
                        principalTable: "Connectors",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IdentityEntities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    UserPrincipalName = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    FirstObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CurrentAsOf = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CurrentAcquisitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityEntities", x => x.Id);
                    table.UniqueConstraint("AK_IdentityEntities_Id_TenantId", x => new { x.Id, x.TenantId });
                });

            migrationBuilder.CreateTable(
                name: "IdentityObservationSetStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AcquisitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Set = table.Column<int>(type: "integer", nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    ObservedCount = table.Column<int>(type: "integer", nullable: false),
                    PreservedCount = table.Column<int>(type: "integer", nullable: false),
                    IsComplete = table.Column<bool>(type: "boolean", nullable: false),
                    Limitation = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityObservationSetStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IdentityObservationSetStates_IdentityAcquisitions_Acquisiti~",
                        columns: x => new { x.AcquisitionId, x.TenantId },
                        principalTable: "IdentityAcquisitions",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IdentityEntityObservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AcquisitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdentityEntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Set = table.Column<int>(type: "integer", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    DisplayNameObserved = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    UserPrincipalNameObserved = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    RolesObserved = table.Column<string>(type: "jsonb", nullable: false),
                    Detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityEntityObservations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IdentityEntityObservations_IdentityAcquisitions_Acquisition~",
                        columns: x => new { x.AcquisitionId, x.TenantId },
                        principalTable: "IdentityAcquisitions",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IdentityEntityObservations_IdentityEntities_IdentityEntityI~",
                        columns: x => new { x.IdentityEntityId, x.TenantId },
                        principalTable: "IdentityEntities",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IdentitySourceLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdentityEntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectorConfigId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<int>(type: "integer", nullable: false),
                    DirectoryNamespace = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FirstLinkedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastAcquisitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentitySourceLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IdentitySourceLinks_IdentityEntities_IdentityEntityId_Tenan~",
                        columns: x => new { x.IdentityEntityId, x.TenantId },
                        principalTable: "IdentityEntities",
                        principalColumns: new[] { "Id", "TenantId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityAcquisitions_ConnectorConfigId_TenantId",
                table: "IdentityAcquisitions",
                columns: new[] { "ConnectorConfigId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityAcquisitions_TenantId_ConnectorConfigId_AcquiredAt",
                table: "IdentityAcquisitions",
                columns: new[] { "TenantId", "ConnectorConfigId", "AcquiredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityAcquisitions_TenantId_DirectoryNamespace_AcquiredAt",
                table: "IdentityAcquisitions",
                columns: new[] { "TenantId", "DirectoryNamespace", "AcquiredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityEntities_TenantId_LastObservedAt",
                table: "IdentityEntities",
                columns: new[] { "TenantId", "LastObservedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityEntityObservations_AcquisitionId_TenantId",
                table: "IdentityEntityObservations",
                columns: new[] { "AcquisitionId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityEntityObservations_IdentityEntityId_TenantId",
                table: "IdentityEntityObservations",
                columns: new[] { "IdentityEntityId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityEntityObservations_TenantId_IdentityEntityId_Observ~",
                table: "IdentityEntityObservations",
                columns: new[] { "TenantId", "IdentityEntityId", "ObservedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_IdentityEntityObservation_Natural",
                table: "IdentityEntityObservations",
                columns: new[] { "TenantId", "AcquisitionId", "IdentityEntityId", "Set" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityObservationSetStates_AcquisitionId_TenantId",
                table: "IdentityObservationSetStates",
                columns: new[] { "AcquisitionId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "UX_IdentityObservationSetState_Natural",
                table: "IdentityObservationSetStates",
                columns: new[] { "TenantId", "AcquisitionId", "Set" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentitySourceLinks_IdentityEntityId_TenantId",
                table: "IdentitySourceLinks",
                columns: new[] { "IdentityEntityId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentitySourceLinks_TenantId_IdentityEntityId",
                table: "IdentitySourceLinks",
                columns: new[] { "TenantId", "IdentityEntityId" });

            migrationBuilder.CreateIndex(
                name: "UX_IdentitySourceLink_Natural",
                table: "IdentitySourceLinks",
                columns: new[] { "TenantId", "DirectoryNamespace", "ExternalId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IdentityEntityObservations");

            migrationBuilder.DropTable(
                name: "IdentityObservationSetStates");

            migrationBuilder.DropTable(
                name: "IdentitySourceLinks");

            migrationBuilder.DropTable(
                name: "IdentityAcquisitions");

            migrationBuilder.DropTable(
                name: "IdentityEntities");

            migrationBuilder.DropColumn(
                name: "IdentityAcquisitionId",
                table: "KnightAssessmentRuns");
        }
    }
}
