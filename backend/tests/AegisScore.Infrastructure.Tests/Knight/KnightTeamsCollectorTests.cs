using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Application.Knight.Reference;
using AegisScore.Connectors.Microsoft.Knight;
using AegisScore.Connectors.Microsoft.Knight.Teams;
using AegisScore.Domain;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-02] O coletor do Microsoft Teams: a tradução da saída do adaptador em capacidades e
/// documentos tipados, e o que acontece quando a coleta NÃO dá certo. Em nenhum caso uma falha vira coleção
/// vazia — o que não foi lido fica declarado, com o desfecho específico.
/// </summary>
public sealed class KnightTeamsCollectorTests
{
    private static readonly Guid Tenant = Guid.Parse("7ea75000-0000-4000-8000-000000000001");

    private static KnightCollectionContext Context() =>
        new(Tenant, new KnightTeamsConfiguration("dir-demo-0001", "client", "secret"));

    private static TeamsKnightCollector CollectorFor(ITeamsAdminReader reader, ITeamsTokenClient? tokens = null) =>
        new(tokens ?? new FakeTokens(), reader);

    [Fact]
    public async Task ColetaCompleta_ProduzUmaCapacidadePorLeitura_ETodosOsDocumentosTipados()
    {
        var result = await CollectorFor(new TeamsCollectionScenario(TeamsCollectionScenario.Variant.Compliant))
            .CollectAsync(Context());

        result.Source.Should().Be(KnightSourceType.MicrosoftTeams);
        result.State.Should().Be(KnightSourceState.Completed);
        result.SourceLabel.Should().Be("Microsoft Teams");
        result.Detail.Should().Contain("módulo Teams PowerShell 6.9.0", "a homologação precisa saber em que módulo a coleta correu");

        result.Capabilities.Select(c => c.Capability).Should().BeEquivalentTo(TeamsKnightCollector.Capabilities);
        result.Capabilities.Should().OnlyContain(c => c.Outcome == KnightCapabilityOutcome.Collected);

        var config = result.TenantConfigurationOrEmpty;
        config.Read<TeamsClientConfiguration>().Single!.AllowEmailIntoChannel.Should().BeFalse();
        config.Read<TeamsFederationConfiguration>().Single!.AllowedDomainsKind.Should().Be("AllowList");
        config.Read<TeamsMeetingPolicyConfiguration>().Items.Should().HaveCount(2);
        config.Read<TeamsMessagingPolicyConfiguration>().Items.Should().ContainSingle();
        config.Read<TeamsAppPermissionPolicyConfiguration>().Items.Should().ContainSingle();
        config.Read<TeamsPolicyAssignment>().Items.Should().ContainSingle(a => a.PolicyName == "Convidados" && a.Rank == 1);

        // O coletor do Teams NÃO produz fato de identidade: a avaliação do Entra ID não é tocada por ele.
        result.Facts.All.Should().BeEmpty();
        result.AffectedObjectSets.Should().BeEmpty();
        result.DirectoryConfiguration.Should().BeNull();
    }

    [Fact]
    public async Task UmaLeituraRecusada_NaoInvalidaAsOutras_EDizOComandoEOMotivo()
    {
        var result = await CollectorFor(new TeamsCollectionScenario(TeamsCollectionScenario.Variant.MeetingPoliciesDenied))
            .CollectAsync(Context());

        result.State.Should().Be(KnightSourceState.PartialCollection);
        var meeting = result.Capabilities.Single(c => c.Capability == KnightCapability.TeamsMeetingPolicies);
        meeting.Outcome.Should().Be(KnightCapabilityOutcome.InsufficientPermission);
        meeting.Detail.Should().Contain("Leitor do Teams").And.Contain("Get-CsTeamsMeetingPolicy");

        result.Capabilities.Where(c => c.Capability != KnightCapability.TeamsMeetingPolicies)
            .Should().OnlyContain(c => c.Outcome == KnightCapabilityOutcome.Collected);

        var read = result.TenantConfigurationOrEmpty.Read<TeamsMeetingPolicyConfiguration>();
        read.Collected.Should().BeFalse("a capacidade falhou — a lista vazia não pode ser lida como 'não há políticas'");
        read.MissingReason.Should().Contain("Leitor do Teams");
    }

    [Fact]
    public async Task ConexaoRecusada_NenhumaLeituraEhTentada_ETodasAsCapacidadesDeclaramOMotivo()
    {
        var result = await CollectorFor(new TeamsCollectionScenario(TeamsCollectionScenario.Variant.NotConnected))
            .CollectAsync(Context());

        result.State.Should().Be(KnightSourceState.InsufficientPermission);
        result.Capabilities.Should().HaveCount(TeamsKnightCollector.Capabilities.Count)
            .And.OnlyContain(c => c.Outcome == KnightCapabilityOutcome.InsufficientPermission);
        // A mensagem é MONTADA a partir da categoria — não é o texto que a fonte devolveu.
        result.Capabilities.Should().OnlyContain(c => c.Detail!.Contains("recusada por permissão ou papel insuficiente"));
        result.Capabilities.Should().OnlyContain(c => c.Detail!.Contains("Leitor do Teams"));
        result.TenantConfigurationOrEmpty.Documents.Should().BeEmpty();
    }

    [Fact]
    public async Task RuntimeIndisponivel_EhFalhaDoTransporte_NaoColetaVazia()
    {
        var result = await CollectorFor(new QuebraTransporte()).CollectAsync(Context());

        result.State.Should().Be(KnightSourceState.Unavailable);
        result.Capabilities.Should().OnlyContain(c => c.Outcome == KnightCapabilityOutcome.Unavailable);
        result.Detail.Should().Contain("PowerShell");
    }

    [Fact]
    public async Task FalhaAoObterToken_NaoChegaAExecutarOAdaptador()
    {
        var reader = new TeamsCollectionScenario(TeamsCollectionScenario.Variant.Compliant);
        var result = await CollectorFor(reader, new FakeTokens(EntraGraphErrorKind.AuthFailure)).CollectAsync(Context());

        result.State.Should().Be(KnightSourceState.AuthenticationFailure);
        reader.Reads.Should().Be(0, "sem token não há o que tentar — e nada é apresentado como coletado");
        result.Capabilities.Should().OnlyContain(c => c.Outcome == KnightCapabilityOutcome.AuthenticationFailure);
    }

    [Fact]
    public async Task FonteNaoConfigurada_NaoColeta()
    {
        var reader = new TeamsCollectionScenario(TeamsCollectionScenario.Variant.Compliant);
        var result = await CollectorFor(reader).CollectAsync(
            new KnightCollectionContext(Tenant, new KnightSourceNotConfigured(KnightSourceType.MicrosoftTeams)));

        result.State.Should().Be(KnightSourceState.NotConfigured);
        reader.Reads.Should().Be(0);
    }

    [Fact]
    public void Cancelamento_Propaga_EmVezDeVirarColetaIncompleta()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        FluentActions.Awaiting(() => CollectorFor(new TeamsCollectionScenario(TeamsCollectionScenario.Variant.Compliant))
                .CollectAsync(Context(), cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void CapacidadesDeclaradas_SaoAsQueOColetorRealmenteProduz()
    {
        // O que a cobertura de implementação usa para dizer "está no fluxo ativo" não pode divergir do coletor.
        KnightCollectorCapabilities.Produces(KnightSourceType.MicrosoftTeams)
            .Should().BeEquivalentTo(TeamsKnightCollector.Capabilities);
    }

    [Fact]
    public void SaidaIlegivelDoAdaptador_EhFalhaDeclarada_NuncaResultadoVazio()
    {
        FluentActions.Invoking(() => PowerShellTeamsAdminReader.Parse("{não é json"))
            .Should().Throw<TeamsAdminTransportException>().WithMessage("*ilegível*");
    }

    /// <summary>Os dois tokens são de RECURSOS diferentes — o do Graph não é reaproveitado no Teams.</summary>
    [Fact]
    public void EscoposDosDoisTokens_SaoDeRecursosDiferentes()
    {
        TeamsTokenClient.GraphScope.Should().Be("https://graph.microsoft.com/.default");
        TeamsTokenClient.TeamsScope.Should().Be("48ac35b8-9aa8-4d74-927d-1f4a14a0b239/.default");
        TeamsTokenClient.TeamsScope.Should().NotBe(TeamsTokenClient.GraphScope);
    }

    /// <summary>O segredo e os tokens nunca aparecem num dump acidental do objeto de configuração.</summary>
    [Fact]
    public void ConfiguracaoECredenciais_NaoImprimemSegredo()
    {
        new KnightTeamsConfiguration("dir", "client", "s3cr3t").ToString().Should().NotContain("s3cr3t").And.Contain("***");
        new TeamsAdminCredentials("dir", "graph-token", "teams-token").ToString()
            .Should().NotContain("graph-token").And.NotContain("teams-token").And.Contain("***");
    }

    // ======================================================================================================
    //  [Revisão dirigida] Omissão de leitura ≠ leitura concluída sem registros
    // ======================================================================================================

    /// <summary>
    /// DEFEITO REPRODUZIDO: o estado da coleta era decidido DEPOIS de descartar as capacidades não tentadas; uma
    /// leitura que o adaptador simplesmente OMITIU deixava o conjunto restante todo "Collected" e a coleta era
    /// declarada CONCLUÍDA. Omissão é ausência de leitura, não leitura sem registros.
    /// </summary>
    [Fact]
    public async Task LeituraOmitidaPeloAdaptador_NaoProduzColetaCompleta()
    {
        var result = await CollectorFor(new TeamsCollectionScenario(TeamsCollectionScenario.Variant.ReadOmitted))
            .CollectAsync(Context());

        result.State.Should().Be(KnightSourceState.PartialCollection);
        result.State.Should().NotBe(KnightSourceState.Completed);

        var omitida = result.Capabilities.Single(c => c.Capability == KnightCapability.TeamsMessagingPolicies);
        omitida.Outcome.Should().Be(KnightCapabilityOutcome.NotAttempted);
        omitida.Detail.Should().Contain("não registrou esta leitura");

        // E o que dependia dela permanece ilegível — nunca "coletou e não achou nada".
        var read = result.TenantConfigurationOrEmpty.Read<TeamsMessagingPolicyConfiguration>();
        read.Collected.Should().BeFalse();
    }

    /// <summary>
    /// Uma leitura que CONCLUIU sem devolver registros continua sendo coleta completa: é o outro lado da
    /// distinção, e ele não pode ser perdido junto com a correção.
    /// </summary>
    [Fact]
    public async Task LeituraConcluidaSemRegistros_ContinuaSendoColetaCompleta()
    {
        var result = await CollectorFor(new ColetaComListaVazia()).CollectAsync(Context());

        result.State.Should().Be(KnightSourceState.Completed);
        result.Capabilities.Should().OnlyContain(c => c.Outcome == KnightCapabilityOutcome.Collected);
        var read = result.TenantConfigurationOrEmpty.Read<TeamsPolicyAssignment>();
        read.Collected.Should().BeTrue("a leitura concluiu");
        read.Items.Should().BeEmpty("e não havia registros");
    }

    // ======================================================================================================
    //  [Revisão dirigida] Identificação ausente não vira "Global" no ADM
    // ======================================================================================================

    /// <summary>
    /// DEFEITO REPRODUZIDO: sem <c>identity</c>, o coletor gravava "Global" no documento — inventando a política
    /// padrão da organização — e os registros sem identidade colidiam no mesmo identificador de documento,
    /// fundindo-se num só. Agora a identidade é preservada como veio (nula) e a posição separa os registros.
    /// </summary>
    [Fact]
    public async Task PoliticasSemIdentificacao_NaoViramGlobal_ENaoSeFundem()
    {
        var result = await CollectorFor(new TeamsCollectionScenario(TeamsCollectionScenario.Variant.UnidentifiedPolicies))
            .CollectAsync(Context());

        var policies = result.TenantConfigurationOrEmpty.Read<TeamsMeetingPolicyConfiguration>();
        policies.Items.Should().HaveCount(3, "três registros distintos chegaram, e três continuam existindo");
        policies.Items.Should().OnlyContain(p => !TeamsPolicyIdentities.IsIdentified(p.Identity));
        policies.Items.Should().NotContain(p => TeamsPolicyIdentities.IsGlobal(p.Identity));

        var ids = result.TenantConfigurationOrEmpty.Documents
            .Where(d => d.Kind == ConfigurationObjectKind.TeamsMeetingPolicy)
            .Select(d => d.ExternalId)
            .ToList();
        ids.Should().HaveCount(3).And.OnlyHaveUniqueItems();
        ids.Should().NotContain("TeamsMeetingPolicy:Global");
    }

    // ======================================================================================================
    //  [Revisão dirigida] Diagnóstico sanitizado — truncar não é sanitizar
    // ======================================================================================================

    /// <summary>
    /// DEFEITO REPRODUZIDO: o texto bruto do erro de conexão era CONCATENADO ao motivo da falha e seguia para o
    /// ADM, para a API e para o relatório. Esse texto é escrito por um módulo de terceiro e pode repetir o
    /// cabeçalho de autorização ou o token da chamada. Aqui o adaptador devolve marcadores SINTÉTICOS de segredo
    /// e nenhum deles pode aparecer no que é persistido ou publicado.
    /// </summary>
    [Fact]
    public async Task SegredoNoDiagnosticoDaConexao_NaoChegaAoResultadoPublicado()
    {
        var result = await CollectorFor(new VazaSegredoNoDiagnostico()).CollectAsync(Context());

        var publicado = string.Join(" | ",
            new[] { result.Detail ?? "" }.Concat(result.Capabilities.Select(c => c.Detail ?? "")));

        publicado.Should().NotContain(VazaSegredoNoDiagnostico.MarcadorDeToken);
        publicado.Should().NotContain(VazaSegredoNoDiagnostico.MarcadorDeCabecalho);
        publicado.Should().NotContain("Bearer ");
        publicado.Should().Contain("Leitor do Teams", "a orientação útil é preservada");
    }

    /// <summary>
    /// O mesmo para a falha de UMA leitura: a mensagem é montada pela categoria e pelo comando, e o
    /// identificador técnico que sobra é sanitizado antes de virar objeto.
    /// </summary>
    [Fact]
    public void IdentificadorDeErroComCaraDeSegredo_EhRedigidoNaLeituraDaSaida()
    {
        var jwt = "eyJhbGciOiJIUzI1NiJ9.QUVHSVMtTUFSQ0FET1ItU0lOVEVUSUNP.c2lnbmF0dXJlLXNpbnRldGljYQ";
        var json = "{\"runtime\":{},\"connected\":false,\"connectionErrorCategory\":\"AuthenticationFailure\","
            + "\"connectionErrorId\":\"" + jwt + "\",\"reads\":[]}";

        var output = PowerShellTeamsAdminReader.Parse(json);
        output.ConnectionErrorId.Should().NotContain(jwt);
        output.ConnectionErrorId.Should().Be("[REDIGIDO]");
    }

    // ---- Duplas de teste ------------------------------------------------------------------------------

    /// <summary>Todas as leituras concluem; a de atribuições devolve uma lista VAZIA (e não uma falha).</summary>
    private sealed class ColetaComListaVazia : ITeamsAdminReader
    {
        public Task<TeamsAdminOutput> ReadAsync(TeamsAdminCredentials credentials, CancellationToken ct = default)
        {
            var reads = TeamsKnightCollector.Capabilities.Select(c => new
            {
                capability = c.ToString(),
                command = "Get-" + c,
                ok = true,
                items = Array.Empty<object>(),
                errorCategory = (string?)null,
                errorId = (string?)null,
            });
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                runtime = new { powerShell = "7.4.6", module = "6.9.0", platform = "Unix" },
                connected = true,
                connectionErrorId = (string?)null,
                connectionErrorCategory = (string?)null,
                reads,
            });
            return Task.FromResult(PowerShellTeamsAdminReader.Parse(json));
        }

        public Task<TeamsAdminOutput> CheckRuntimeAsync(CancellationToken ct = default) => ReadAsync(null!, ct);
    }

    /// <summary>
    /// Adaptador que devolve, no diagnóstico da conexão, marcadores SINTÉTICOS com a forma de segredo. Nenhuma
    /// credencial real: são cadeias inventadas para este teste.
    /// </summary>
    private sealed class VazaSegredoNoDiagnostico : ITeamsAdminReader
    {
        internal const string MarcadorDeToken = "eyJhZWdpcyI6InNpbnRldGljbyJ9.QUVHSVMtVEVTVEUtU0lOVEVUSUNP.YXNzaW5hdHVyYQ";
        internal const string MarcadorDeCabecalho = "Authorization: Bearer AEGIS-MARCADOR-SINTETICO-0123456789";

        public Task<TeamsAdminOutput> ReadAsync(TeamsAdminCredentials credentials, CancellationToken ct = default)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                runtime = new { powerShell = "7.4.6", module = "6.9.0", platform = "Unix" },
                connected = false,
                connectionErrorCategory = "InsufficientPermission",
                connectionErrorId = MarcadorDeToken + " " + MarcadorDeCabecalho,
                reads = Array.Empty<object>(),
            });
            return Task.FromResult(PowerShellTeamsAdminReader.Parse(json));
        }

        public Task<TeamsAdminOutput> CheckRuntimeAsync(CancellationToken ct = default) => ReadAsync(null!, ct);
    }

    private sealed class FakeTokens : ITeamsTokenClient
    {
        private readonly EntraGraphErrorKind? _fail;
        public FakeTokens(EntraGraphErrorKind? fail = null) => _fail = fail;

        public Task<TeamsAdminCredentials> AcquireAsync(IMicrosoftGraphCredentials credentials, CancellationToken ct = default) =>
            _fail is { } kind
                ? throw new EntraGraphException(kind, "token endpoint retornou 401", 401, endpointPath: "/oauth2/v2.0/token")
                : Task.FromResult(new TeamsAdminCredentials(credentials.AzureTenantId, "graph", "teams"));
    }

    private sealed class QuebraTransporte : ITeamsAdminReader
    {
        public Task<TeamsAdminOutput> ReadAsync(TeamsAdminCredentials credentials, CancellationToken ct = default) =>
            throw new TeamsAdminTransportException(
                "O runtime do PowerShell não pôde ser iniciado neste ambiente. A coleta do Microsoft Teams não foi tentada.");

        public Task<TeamsAdminOutput> CheckRuntimeAsync(CancellationToken ct = default) => ReadAsync(null!, ct);
    }
}
