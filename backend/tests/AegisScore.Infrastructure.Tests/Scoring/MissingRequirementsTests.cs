using AegisScore.Application.Abstractions;
using AegisScore.Application.Services;
using AegisScore.Domain;
using AegisScore.Infrastructure.Persistence;
using AegisScore.Infrastructure.Scoring;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Scoring;

/// <summary>
/// Lacunas de evidência TIPADAS no ledger (<see cref="MissingRequirement"/>): a distinção entre "o SOC
/// não emite o log" e "a política nunca foi escrita". Rodam sobre SQLite in-memory, como o resto da
/// suíte do writer — banco relacional real, exercitando o ValueConverter jsonb de verdade.
/// </summary>
public sealed class MissingRequirementsTests : IDisposable
{
    private const int MaxPoints = 20;
    private const string SubCode = "PR.AA-01";
    private static readonly Guid TenantA = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly SqliteConnection _connection;

    public MissingRequirementsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(TenantA);
        ctx.Database.EnsureCreated();
        SeedCatalog(ctx);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task LacunasSobrevivemAoRoundTripDoBanco_ComTipagemPreservada()
    {
        var lacunas = new[]
        {
            new MissingRequirement(ComplianceRequirementType.Telemetry, "EntraID",
                "Sinal de MFA privilegiado não é coletado — conector sem permissão de leitura."),
            new MissingRequirement(ComplianceRequirementType.Documentation, "Policy_Access_Control",
                "Política de controle de acesso não localizada no repositório de governança."),
            new MissingRequirement(ComplianceRequirementType.Both, "PAM_Rollout",
                "Cofre de credenciais: falta a norma aprovada E a telemetria de uso."),
        };

        await using (var db = NewContext(TenantA))
            await WriterFor(db).ApplyVerdictAsync(
                TenantA, SubCode, ControlStatus.NonCompliant, "telemetria: 4 admins sem MFA",
                VerdictSource.Telemetry, missingRequirements: lacunas);

        await using var assert = NewContext(TenantA);
        var state = await assert.TenantControlStates.SingleAsync();

        // Volta TIPADO — o consumidor percorre e agrega por Type sem desserializar à mão.
        state.MissingRequirements.Should().BeEquivalentTo(lacunas);
        state.MissingRequirements
            .Count(m => m.Type == ComplianceRequirementType.Documentation)
            .Should().Be(1, "a agregação por natureza da lacuna é o caso de uso que motivou a estrutura");
    }

    [Fact]
    public async Task EnumEhPersistidoComoTEXTO_NaoComoOrdinal()
    {
        // A decisão de auditabilidade: o ledger é consultado direto no SQL, e {"Type":1} é ilegível —
        // pior, reordenar o enum reinterpretaria em silêncio o histórico já gravado.
        await using (var db = NewContext(TenantA))
            await WriterFor(db).ApplyVerdictAsync(
                TenantA, SubCode, ControlStatus.NonCompliant, "sem política",
                VerdictSource.Documentary,
                missingRequirements: new[]
                {
                    new MissingRequirement(
                        ComplianceRequirementType.Documentation, "Policy_Access_Control", "Ausente."),
                });

        // Lê a coluna CRUA, contornando o ValueConverter — é o conteúdo no disco que está sob teste.
        await using var raw = _connection.CreateCommand();
        raw.CommandText = "SELECT MissingRequirements FROM TenantControlStates LIMIT 1";
        var json = (string)(await raw.ExecuteScalarAsync())!;

        json.Should().Contain("\"Documentation\"", "o enum tem de viajar por nome no jsonb");
        json.Should().NotContain("\"Type\":1", "ordinal no ledger torna a auditoria SQL ilegível e frágil");
    }

    [Fact]
    public async Task ControleConforme_ZeraAsLacunas_MesmoQueOChamadorAsEnvie()
    {
        // 1) Estado não-conforme com pendência registrada.
        await using (var db = NewContext(TenantA))
            await WriterFor(db).ApplyVerdictAsync(
                TenantA, SubCode, ControlStatus.NonCompliant, "4 admins sem MFA", VerdictSource.Telemetry,
                missingRequirements: new[]
                {
                    new MissingRequirement(ComplianceRequirementType.Telemetry, "EntraID", "Sem sinal de MFA."),
                });

        // 2) O controle é corrigido — e o chamador, por engano, ainda manda a lacuna antiga.
        await using (var db = NewContext(TenantA))
        {
            var verdict = await WriterFor(db).ApplyVerdictAsync(
                TenantA, SubCode, ControlStatus.Compliant, "MFA em 100% dos privilegiados",
                VerdictSource.Telemetry,
                missingRequirements: new[]
                {
                    new MissingRequirement(ComplianceRequirementType.Telemetry, "EntraID", "Sem sinal de MFA."),
                });

            verdict.MissingRequirements.Should().BeEmpty();
        }

        // 3) A invariante é do LEDGER, não do chamador: o banco não guarda pendência de controle conforme.
        await using var assert = NewContext(TenantA);
        (await assert.TenantControlStates.SingleAsync()).MissingRequirements.Should().BeEmpty();
    }

    [Fact]
    public async Task MitigadoPorTerceiro_PRESERVA_asLacunas()
    {
        // O contraponto do caso acima: risco coberto por terceiro NÃO é lacuna própria fechada — a
        // organização segue devendo a prova, e apagá-la aqui esconderia dívida real de conformidade.
        var lacuna = new MissingRequirement(
            ComplianceRequirementType.Documentation, "Policy_Access_Control",
            "Controle operado pelo MSSP; contrato e política internos ainda não formalizados.");

        await using (var db = NewContext(TenantA))
            await WriterFor(db).ApplyVerdictAsync(
                TenantA, SubCode, ControlStatus.MitigatedByThirdParty, "MSSP opera o IAM",
                VerdictSource.Telemetry, missingRequirements: new[] { lacuna });

        await using var assert = NewContext(TenantA);
        var state = await assert.TenantControlStates.SingleAsync();
        state.CurrentScore.Should().Be(MaxPoints / 2);
        state.MissingRequirements.Should().ContainSingle().Which.Should().Be(lacuna);
    }

    [Fact]
    public async Task VerdictoDocumentalRecusado_DevolveAsLacunasVIGENTES_NaoAsPropostas()
    {
        var vigente = new MissingRequirement(
            ComplianceRequirementType.Documentation, "Policy_Access_Control", "Política ausente.");

        // Telemetria estabelece o estado autoritativo, com sua lacuna.
        await using (var db = NewContext(TenantA))
            await WriterFor(db).ApplyVerdictAsync(
                TenantA, SubCode, ControlStatus.NonCompliant, "4 admins sem MFA", VerdictSource.Telemetry,
                missingRequirements: new[] { vigente });

        // Um PDF tenta subir o controle e traz OUTRA lista. A precedência recusa a escrita inteira.
        await using (var db = NewContext(TenantA))
        {
            var verdict = await WriterFor(db).ApplyVerdictAsync(
                TenantA, SubCode, ControlStatus.MitigatedByThirdParty, "política vigente",
                VerdictSource.Documentary,
                missingRequirements: new[]
                {
                    new MissingRequirement(ComplianceRequirementType.Telemetry, "Sentinel", "Proposta."),
                });

            verdict.MissingRequirements.Should().ContainSingle().Which.Should().Be(vigente,
                "o retorno descreve o estado que ficou de pé, não o veredito recusado");
        }
    }

    [Fact]
    public async Task SemLacunasInformadas_PersisteListaVazia_NuncaNulo()
    {
        await using (var db = NewContext(TenantA))
            await WriterFor(db).ApplyVerdictAsync(
                TenantA, SubCode, ControlStatus.NonCompliant, "evidência insuficiente", VerdictSource.Telemetry);

        await using var assert = NewContext(TenantA);
        var state = await assert.TenantControlStates.SingleAsync();
        state.MissingRequirements.Should().NotBeNull().And.BeEmpty(
            "nenhum consumidor deve precisar de checagem de nulo nesta coleção");
    }

    // ---- harness (espelha ControlStateWriterTests) --------------------------------

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options,
            new SystemTenantContext(tenantId));

    private IControlStateWriter WriterFor(AegisScoreDbContext db) =>
        new ControlStateWriter(db, new SystemTenantContext(TenantA), NullLogger<ControlStateWriter>.Instance);

    private static void SeedCatalog(AegisScoreDbContext ctx)
    {
        var fv = new FrameworkVersion { Name = "NIST CSF 2.0", IsActive = true };
        var fn = new NistFunction { Code = "PR", Name = "PROTECT" };
        var cat = new NistCategory { Code = "PR.AA", Name = "Identity" };
        cat.Subcategories.Add(new NistSubcategory
        {
            Code = SubCode,
            Description = "Identities and credentials are managed.",
            MaxScorePoints = MaxPoints,
        });
        fn.Categories.Add(cat);
        fv.Functions.Add(fn);

        ctx.FrameworkVersions.Add(fv);
        ctx.SaveChanges();
    }
}
