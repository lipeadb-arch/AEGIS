using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Catalog;
using AegisScore.Domain;
using AegisScore.Infrastructure.Identity;
using AegisScore.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-01] ABRANGÊNCIA das revisões de acesso, pelo caminho inteiro (resposta do Microsoft Graph →
/// coletor → ADM → releitura → avaliação). Existir uma revisão não comprova que ela alcança a população que o
/// critério exige: o escopo (<c>scope</c>, <c>instanceEnumerationScope</c>) e as etapas (<c>stageSettings</c>, que
/// substituem revisores e configurações do nível superior) precisam ser lidos e interpretados. Quando a informação
/// necessária falta ou não pode ser interpretada com segurança, o controle fica NÃO AVALIADO com a limitação — nunca
/// aprovado. As reprovações demonstráveis são preservadas.
/// Formato das respostas conforme a documentação da versão estável (v1.0) do Microsoft Graph:
/// accessReviewScheduleDefinition, accessReviewStageSettings, recurrencePattern e recurrenceRange.
/// </summary>
public sealed class KnightAccessReviewScopeTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("cccccccc-5353-5353-5353-535353535353");
    private const string Group = "11111111-2222-3333-4444-555555555555";

    private readonly SqliteConnection _connection;
    private readonly ITestOutputHelper _output;

    public KnightAccessReviewScopeTests(ITestOutputHelper output)
    {
        _output = output;
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var ctx = NewContext(null);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    // ---- Convidados (AK-ENTRA-053) ---------------------------------------------------------------------

    [Fact]
    public async Task Convidados_RevisaoLimitadaAUmGrupo_NaoComprovaTodosOsConvidados()
    {
        var run = await RunAsync(Review("ar-1", "Convidados do projeto", GuestsOfOneGroup(Group)));

        var i = Indicator(run, "AK-ENTRA-053");
        i.Status.Should().Be(KnightIndicatorStatus.Exposed,
            "o escopo alcança os convidados de um grupo; a referência pede todos os convidados");
        i.Evidence.Should().Contain("grupo");
    }

    [Fact]
    public async Task Convidados_RevisaoDeTodosOsGruposDoMicrosoft365_Aprova()
    {
        var run = await RunAsync(Review("ar-1", "Convidados de todos os grupos", GuestsOfAllGroups(), AllUnifiedGroups()));

        Indicator(run, "AK-ENTRA-053").Status.Should().Be(KnightIndicatorStatus.Passed);
    }

    [Fact]
    public async Task Convidados_SemEnumeracaoDeInstancias_NaoAprova_EDizAInformacaoQueFalta()
    {
        // Escopo relativo ("./members/...") sem instanceEnumerationScope: a documentação exige essa propriedade para
        // dizer QUAIS grupos entram na revisão. Sem ela, a população não pode ser afirmada nem negada.
        var run = await RunAsync(Review("ar-1", "Convidados", GuestsOfAllGroups()));

        var i = Indicator(run, "AK-ENTRA-053");
        i.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        i.NotEvaluatedReason.Should().Contain("instanceEnumerationScope");
    }

    [Fact]
    public async Task Convidados_RecorrenciaSemanalACadaOitoSemanas_NaoAtendeMensalOuMaisFrequente()
    {
        var run = await RunAsync(Review("ar-1", "Convidados", GuestsOfAllGroups(), AllUnifiedGroups(),
            recurrence: Recurrence("weekly", 8)));

        var i = Indicator(run, "AK-ENTRA-053");
        i.Status.Should().Be(KnightIndicatorStatus.Exposed, "oito semanas é menos frequente que mensal");
        i.Evidence.Should().Contain("recorrência");
    }

    [Fact]
    public async Task Convidados_IntervaloAusente_NaoPresumeUm_EFicaNaoAvaliado()
    {
        // A documentação declara "interval" obrigatório no recurrencePattern; ausente, a frequência é desconhecida.
        var run = await RunAsync(Review("ar-1", "Convidados", GuestsOfAllGroups(), AllUnifiedGroups(),
            recurrence: Recurrence("absoluteMonthly", null)));

        var i = Indicator(run, "AK-ENTRA-053");
        i.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        i.NotEvaluatedReason.Should().Contain("intervalo");
    }

    [Fact]
    public async Task Convidados_SerieEncerrada_NaoAprova()
    {
        var run = await RunAsync(Review("ar-1", "Convidados", GuestsOfAllGroups(), AllUnifiedGroups(),
            recurrence: Recurrence("absoluteMonthly", 1, range: new { type = "endDate", startDate = "2024-01-01", endDate = "2024-06-30" })));

        Indicator(run, "AK-ENTRA-053").Status.Should().Be(KnightIndicatorStatus.Exposed);
    }

    [Fact]
    public async Task Convidados_EtapasDefinemOsRevisores_Aprova()
    {
        // stageSettings substitui os revisores do nível superior: uma revisão em etapas com revisores em cada etapa
        // atende ao critério mesmo com "reviewers" vazio no nível superior.
        var run = await RunAsync(Review("ar-1", "Convidados em duas etapas", GuestsOfAllGroups(), AllUnifiedGroups(),
            reviewers: Array.Empty<object>(),
            stages: new object[]
            {
                Stage("1", 7, new[] { "/users/u1" }),
                Stage("2", 7, new[] { "/users/u2" }, dependsOn: new[] { "1" }),
            }));

        Indicator(run, "AK-ENTRA-053").Status.Should().Be(KnightIndicatorStatus.Passed);
    }

    [Fact]
    public async Task Convidados_EtapaSemRevisores_EAutorrevisao_NaoAprova()
    {
        // A etapa sem revisores substitui os do nível superior e vira autorrevisão: quem é revisado decide.
        var run = await RunAsync(Review("ar-1", "Convidados em duas etapas", GuestsOfAllGroups(), AllUnifiedGroups(),
            reviewers: new object[] { new { query = "/users/u1", queryType = "MicrosoftGraph" } },
            stages: new object[]
            {
                Stage("1", 7, new[] { "/users/u1" }),
                Stage("2", 7, Array.Empty<string>(), dependsOn: new[] { "1" }),
            }));

        var i = Indicator(run, "AK-ENTRA-053");
        i.Status.Should().Be(KnightIndicatorStatus.Exposed);
        i.Evidence.Should().Contain("revisores");
    }

    [Fact]
    public async Task Convidados_EscopoNaoInterpretavel_NaoAprovaENaoReprova()
    {
        var run = await RunAsync(Review("ar-1", "Convidados", new Dictionary<string, object>
        {
            ["@odata.type"] = "#microsoft.graph.accessReviewQueryScope",
            ["query"] = "/identityGovernance/accessReviews/legado?$filter=(userType eq 'Guest')",
            ["queryType"] = "MicrosoftGraph",
        }));

        var i = Indicator(run, "AK-ENTRA-053");
        i.Status.Should().Be(KnightIndicatorStatus.NotEvaluated);
        i.NotEvaluatedReason.Should().Contain("escopo");
    }

    [Fact]
    public async Task Convidados_UmaRevisaoInterpretavelReprovadaEOutraIndeterminada_NaoReprovaOControle()
    {
        var run = await RunAsync(
            Review("ar-1", "Convidados de todos os grupos", GuestsOfAllGroups(), AllUnifiedGroups(), removeAccess: false),
            Review("ar-2", "Convidados (legado)", new Dictionary<string, object>
            {
                ["@odata.type"] = "#microsoft.graph.accessReviewQueryScope",
                ["query"] = "/identityGovernance/legado?$filter=(userType eq 'Guest')",
                ["queryType"] = "MicrosoftGraph",
            }));

        var i = Indicator(run, "AK-ENTRA-053");
        i.Status.Should().Be(KnightIndicatorStatus.NotEvaluated,
            "uma das revisões não pôde ser interpretada: ela pode ser a que cumpre o critério");
        i.NotEvaluatedReason.Should().Contain("ar-2").And.NotContain("ar-1");
    }

    // ---- Papéis (AK-ENTRA-054 e AK-ENTRA-030) ----------------------------------------------------------

    [Fact]
    public async Task Papeis_RevisaoDeTodasAsAtribuicoesDeUsuario_Aprova()
    {
        var run = await RunAsync(ReviewedRoleDefinitions().ToArray());

        Indicator(run, "AK-ENTRA-054").Status.Should().Be(KnightIndicatorStatus.Passed);
    }

    [Fact]
    public async Task Papeis_RevisaoLimitadaAConvidados_NaoComprovaAsAtribuicoesDoPapel()
    {
        var reviews = ReviewedRoleDefinitions(EntraConfigurationControls.GlobalAdministratorTemplateId,
            role => Review("ar-ga", "Administrador Global (convidados)", RoleForGuestsOnly(role))).ToArray();

        var run = await RunAsync(reviews);

        var i = Indicator(run, "AK-ENTRA-054");
        i.Status.Should().Be(KnightIndicatorStatus.Exposed);
        i.AffectedObjectCount.Should().Be(1);
        i.Evidence.Should().Contain("1 de 5");
    }

    [Fact]
    public async Task Papeis_RevisaoSoDasAtribuicoesAtivas_NaoComprovaAsElegiveis()
    {
        var reviews = ReviewedRoleDefinitions(EntraConfigurationControls.GlobalAdministratorTemplateId,
            role => Review("ar-ga", "Administrador Global (ativas)", RoleActiveAssignmentsOnly(role))).ToArray();

        var run = await RunAsync(reviews);

        Indicator(run, "AK-ENTRA-054").Status.Should().Be(KnightIndicatorStatus.Exposed);
    }

    [Fact]
    public async Task CriadorDeLocatario_RevisaoSoDasAtribuicoesAtivas_NaoAprova()
    {
        var run = await RunAsync(Review("ar-tc", "Criador de Locatário",
            RoleActiveAssignmentsOnly(EntraConfigurationControls.TenantCreatorTemplateId)));

        Indicator(run, "AK-ENTRA-030").Status.Should().Be(KnightIndicatorStatus.Exposed);
    }

    [Fact]
    public async Task CriadorDeLocatario_RevisaoDoPapelInteiro_Aprova()
    {
        var run = await RunAsync(Review("ar-tc", "Criador de Locatário",
            RoleForAllUsers(EntraConfigurationControls.TenantCreatorTemplateId)));

        Indicator(run, "AK-ENTRA-030").Status.Should().Be(KnightIndicatorStatus.Passed);
    }

    // ---- Montagem das definições (formato documentado do Microsoft Graph) -------------------------------

    private static object Recurrence(string? type, int? interval, object? range = null)
    {
        var pattern = new Dictionary<string, object>();
        if (type is not null) pattern["type"] = type;
        if (interval is not null) pattern["interval"] = interval.Value;
        return new Dictionary<string, object>
        {
            ["pattern"] = pattern,
            ["range"] = range ?? new { type = "noEnd", startDate = "2026-01-01" },
        };
    }

    private static object Stage(string stageId, int durationInDays, IReadOnlyList<string> reviewers, string[]? dependsOn = null)
    {
        var stage = new Dictionary<string, object>
        {
            ["stageId"] = stageId,
            ["durationInDays"] = durationInDays,
            ["recommendationsEnabled"] = true,
            ["reviewers"] = reviewers.Select(r => new { query = r, queryType = "MicrosoftGraph" }).ToArray(),
        };
        if (dependsOn is not null) stage["dependsOn"] = dependsOn;
        return stage;
    }

    private static object Review(
        string id, string name, object scope, object? instanceEnumerationScope = null, object? recurrence = null,
        object[]? reviewers = null, object[]? stages = null, string status = "InProgress", bool removeAccess = true)
    {
        var settings = new Dictionary<string, object>
        {
            ["instanceDurationInDays"] = 14,
            ["autoApplyDecisionsEnabled"] = true,
            ["justificationRequiredOnApproval"] = true,
            ["mailNotificationsEnabled"] = true,
            ["recurrence"] = recurrence ?? Recurrence("absoluteMonthly", 1),
            ["applyActions"] = removeAccess
                ? new object[] { new Dictionary<string, object> { ["@odata.type"] = "#microsoft.graph.removeAccessApplyAction" } }
                : Array.Empty<object>(),
        };
        var def = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["displayName"] = name,
            ["status"] = status,
            ["scope"] = scope,
            ["reviewers"] = reviewers ?? new object[] { new { query = "/users/u1", queryType = "MicrosoftGraph" } },
            ["settings"] = settings,
        };
        if (instanceEnumerationScope is not null) def["instanceEnumerationScope"] = instanceEnumerationScope;
        if (stages is not null) def["stageSettings"] = stages;
        return def;
    }

    private static object GuestsOfAllGroups() => new Dictionary<string, object>
    {
        ["@odata.type"] = "#microsoft.graph.accessReviewQueryScope",
        ["query"] = "./members/microsoft.graph.user/?$count=true&$filter=(userType eq 'Guest')",
        ["queryType"] = "MicrosoftGraph",
    };

    private static object AllUnifiedGroups() => new Dictionary<string, object>
    {
        ["@odata.type"] = "#microsoft.graph.accessReviewQueryScope",
        ["query"] = "/groups?$filter=(groupTypes/any(c:c eq 'Unified'))&$count=true",
        ["queryType"] = "MicrosoftGraph",
    };

    private static object GuestsOfOneGroup(string groupId) => new Dictionary<string, object>
    {
        ["@odata.type"] = "#microsoft.graph.accessReviewQueryScope",
        ["query"] = $"/groups/{groupId}/transitiveMembers/?$filter=(userType eq 'Guest')",
        ["queryType"] = "MicrosoftGraph",
    };

    private static object RoleForAllUsers(string role) => new Dictionary<string, object>
    {
        ["@odata.type"] = "#microsoft.graph.principalResourceMembershipsScope",
        ["principalScopes"] = new object[] { new { query = "/users", queryType = "MicrosoftGraph" } },
        ["resourceScopes"] = new object[] { new { query = "/roleManagement/directory/roleDefinitions/" + role, queryType = "MicrosoftGraph" } },
    };

    private static object RoleForGuestsOnly(string role) => new Dictionary<string, object>
    {
        ["@odata.type"] = "#microsoft.graph.principalResourceMembershipsScope",
        ["principalScopes"] = new object[] { new { query = "/users?$filter=(userType eq 'Guest')", queryType = "MicrosoftGraph" } },
        ["resourceScopes"] = new object[] { new { query = "/roleManagement/directory/roleDefinitions/" + role, queryType = "MicrosoftGraph" } },
    };

    private static object RoleActiveAssignmentsOnly(string role) => new Dictionary<string, object>
    {
        ["@odata.type"] = "#microsoft.graph.accessReviewQueryScope",
        ["query"] = "/roleManagement/directory/roleAssignmentScheduleInstances?$expand=principal&$filter=(assignmentType eq 'Assigned' "
            + $"and isof(principal,'microsoft.graph.user') and roleDefinitionId eq '{role}')",
        ["queryType"] = "MicrosoftGraph",
    };

    /// <summary>Uma revisão íntegra por papel revisado, salvo o papel que o teste substitui.</summary>
    private static IEnumerable<object> ReviewedRoleDefinitions(string? replaced = null, Func<string, object>? replacement = null)
    {
        foreach (var (template, name, _) in EntraConfigurationControls.ReviewedRoles)
            yield return string.Equals(template, replaced, StringComparison.OrdinalIgnoreCase) && replacement is not null
                ? replacement(template)
                : Review("ar-" + template[..8], "Revisão de " + name, RoleForAllUsers(template));
    }

    // ---- Infraestrutura -----------------------------------------------------------------------------

    private static KnightIndicatorView Indicator(KnightAssessment run, string id) =>
        run.Indicators.Single(i => i.IndicatorId == id);

    private async Task<KnightAssessment> RunAsync(params object[] definitions)
    {
        var body = JsonSerializer.Serialize(new { value = definitions });
        await SeedAsync();
        await using var db = NewContext(TenantA);
        var scenario = new EntraConfigurationScenario(EntraConfigurationScenario.Variant.Compliant,
            url => url.Contains("accessReviews/definitions") ? (HttpStatusCode.OK, body) : null);
        var run = await KnightMulticloudReportTests.ServiceFor(db, TenantA, scenario.Handler())
            .RunAssessmentAsync(KnightSourceType.MicrosoftEntraId);
        foreach (var id in new[] { "AK-ENTRA-030", "AK-ENTRA-053", "AK-ENTRA-054" })
        {
            var i = Indicator(run, id);
            _output.WriteLine($"{id} {i.Status} [{i.AffectedObjectCount}] {i.Evidence}");
        }
        return run;
    }

    private AegisScoreDbContext NewContext(Guid? tenantId) =>
        new(new DbContextOptionsBuilder<AegisScoreDbContext>().UseSqlite(_connection).Options, new SystemTenantContext(tenantId));

    private async Task SeedAsync()
    {
        await using (var db = NewContext(null))
        {
            if (await db.Tenants.AnyAsync(t => t.Id == TenantA)) return;
            db.Tenants.Add(new Tenant { Id = TenantA, Name = "Cliente Demo", Slug = $"t-{TenantA:N}", Status = TenantStatus.Active });
            await db.SaveChangesAsync();
        }
        await using var dbt = NewContext(TenantA);
        dbt.Connectors.Add(new ConnectorConfig
        {
            TenantId = TenantA, Provider = ConnectorProvider.Microsoft, Capability = ConnectorCapability.IdentityPosture,
            DisplayName = "Microsoft Entra ID", Enabled = true, EncryptedSettings = "cifrado",
        });
        await dbt.SaveChangesAsync();
    }
}
