using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Abstractions;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Connectors.Microsoft.Knight;
using AegisScore.Connectors.Microsoft.Knight.Services;
using AegisScore.Connectors.Microsoft.Knight.Teams;
using AegisScore.Domain;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-TEAMS-FABRIC-ACCESS-01] Acesso efetivo do KNIGHT ao Teams e ao Fabric. Reproduz os defeitos observados no
/// relatório real de 27/10 (Teams: 17 controles com "confira o papel Leitor do Teams" deduzido de uma recusa genérica; Fabric:
/// 403 InsufficientScopes sem nenhuma verificação do que a aplicação tem) e prova as correções: a etapa exata da recusa, os
/// códigos OAuth seguros, o que o token e a entidade de serviço COMPROVAM e a ação só quando a evidência a demonstra.
///
/// Tudo aqui é sintético: tokens JWT montados no teste (sem assinatura, sem valor fora do teste), respostas HTTP simuladas.
/// Nenhum teste prova autenticação contra a Microsoft.
/// </summary>
public sealed class KnightTeamsFabricAccessTests
{
    internal const string TenantGuid = "7ea75000-0000-4000-8000-0000000000aa";
    internal const string AppGuid = "a9900000-0000-4000-8000-0000000000aa";
    internal const string SpObjectId = "5b000000-0000-4000-8000-0000000000aa";
    private const string Trace = "11111111-2222-4333-8444-555555555555";
    private const string Correlation = "66666666-7777-4888-9999-000000000000";
    private const string SecretMarker = "AEGIS-SEGREDO-SINTETICO-OAUTH-0001";

    private static readonly Guid Tenant = Guid.Parse("7ea75000-0000-4000-8000-000000000001");

    private static KnightTeamsConfiguration TeamsConfig() => new(TenantGuid, AppGuid, "secret");

    // ---- Tokens sintéticos -----------------------------------------------------------------------------

    /// <summary>JWT sintético (alg none, assinatura fictícia) com as claims pedidas.</summary>
    internal static string Jwt(string aud, string? tid = TenantGuid, string? appid = AppGuid,
        string[]? roles = null, string[]? wids = null, string? extra = null)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = new Dictionary<string, object?> { ["aud"] = aud, ["tid"] = tid, ["appid"] = appid, ["idtyp"] = "app" };
        if (roles is not null) payload["roles"] = roles;
        if (wids is not null) payload["wids"] = wids;
        if (extra is not null) payload["xms_sintetico"] = extra;
        return B64("{\"alg\":\"none\",\"typ\":\"JWT\"}") + "." + B64(JsonSerializer.Serialize(payload)) + ".c2ludGV0aWNv";
    }

    internal static string GraphToken(params string[] roles) => Jwt("https://graph.microsoft.com", roles: roles);

    internal static string TeamsToken(string[]? wids = null, string[]? roles = null) =>
        Jwt("48ac35b8-9aa8-4d74-927d-1f4a14a0b239", wids: wids ?? Array.Empty<string>(), roles: roles);

    // =====================================================================================================
    //  1. OAuth: códigos seguros preservados, descrição e corpo descartados; qual dos dois tokens falhou
    // =====================================================================================================

    [Fact]
    public async Task TeamsTokenClient_SegundoTokenRecusado_PreservaErrorAadstsTraceECorrelacao_NuncaADescricao()
    {
        var handler = new TokenHandler(failScopeContains: "48ac35b8");
        var client = new TeamsTokenClient(new HttpClient(handler));

        var ex = await FluentActions.Awaiting(() => client.AcquireAsync(TeamsConfig())).Should().ThrowAsync<EntraGraphException>();

        // Ordem e recursos da documentação: Graph primeiro, depois a administração do Teams; tenant e aplicação configurados.
        handler.Scopes.Should().Equal("https://graph.microsoft.com/.default", "48ac35b8-9aa8-4d74-927d-1f4a14a0b239/.default");
        handler.Urls.Should().OnlyContain(u => u == $"https://login.microsoftonline.com/{TenantGuid}/oauth2/v2.0/token");
        handler.Bodies.Should().OnlyContain(b => b.Contains("client_id=" + AppGuid));

        var e = ex.Which;
        e.TokenResource.Should().Be(TeamsTokenClient.TeamsResource, "a etapa exata é o SEGUNDO token");
        e.GraphErrorCode.Should().Be("invalid_client");
        e.OAuthErrorCodes.Should().Equal("AADSTS7000215");
        e.ServiceRequestId.Should().Be(Trace);
        e.ServiceCorrelationId.Should().Be(Correlation);

        var failure = KnightFailureClassifier.FromHttp(e, "Microsoft Teams", "token");
        failure.Outcome.Should().Be(KnightCapabilityOutcome.AuthenticationFailure);
        failure.Reason.Should().Contain("recusou a emissão do token de Skype and Teams Tenant Admin API")
            .And.Contain("invalid_client").And.Contain("AADSTS7000215").And.Contain(Trace).And.Contain(Correlation);
        failure.Reason.Should().NotContain(SecretMarker).And.NotContain("Invalid client secret", "a descrição OAuth nunca atravessa");
        failure.Diagnostic.ServiceCorrelationId.Should().Be(Correlation);
        failure.Diagnostic.Code.Should().Be("invalid_client/AADSTS7000215");
    }

    [Fact]
    public async Task AppTokenClient_TokenDoFabricRecusado_DizORecurso_ECodigosSeguros()
    {
        var client = new MicrosoftAppTokenClient(new HttpClient(new TokenHandler(failScopeContains: "api.fabric")));

        var ex = await FluentActions.Awaiting(() => client.AcquireAsync(TeamsConfig(), FabricKnightCollector.Scope))
            .Should().ThrowAsync<EntraGraphException>();

        ex.Which.TokenResource.Should().Be("API do Microsoft Fabric");
        ex.Which.OAuthErrorCodes.Should().Equal("AADSTS7000215");
        KnightFailureClassifier.FromHttp(ex.Which, "Microsoft Fabric", "token").Reason
            .Should().Contain("recusou a emissão do token de API do Microsoft Fabric").And.NotContain(SecretMarker);
    }

    [Fact]
    public void OAuthTokenError_DescartaValoresForaDaForma()
    {
        var body = """{"error":"invalid client; DROP","error_codes":["7000215", -1, 7000215.5, 50034],"trace_id":"não-é-guid","correlation_id":"eyJhbGciOi.x.y"}""";
        var parsed = OAuthTokenError.Read(body);
        parsed.Error.Should().BeNull("error fora do vocabulário OAuth é descartado");
        parsed.Codes.Should().Equal("AADSTS50034");
        parsed.TraceId.Should().BeNull();
        parsed.CorrelationId.Should().BeNull();
    }

    // =====================================================================================================
    //  2. Falha OAuth distinta de recusa de comando — no coletor
    // =====================================================================================================

    [Fact]
    public async Task Coletor_FalhaOAuth_NenhumaSessaoAberta_ComARecusaDoToken()
    {
        var reader = new ScriptedTeamsReader(connected: true);
        var collector = new TeamsKnightCollector(new TeamsTokenClient(new HttpClient(new TokenHandler(failScopeContains: "48ac35b8"))), reader);

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, TeamsConfig()));

        result.State.Should().Be(KnightSourceState.AuthenticationFailure);
        reader.Calls.Should().Be(0, "sem os dois tokens o adaptador nem é iniciado");
        result.Capabilities.Should().OnlyContain(c => c.Outcome == KnightCapabilityOutcome.AuthenticationFailure
            && c.Detail!.Contains("recusou a emissão do token de Skype and Teams Tenant Admin API")
            && c.Detail.Contains("A sessão do Teams não foi aberta")
            && c.Diagnostic!.Stage == "token");
    }

    [Fact]
    public async Task Coletor_ComandoRecusado_DizAEtapaOStatusEACorrelacao_SemDeduzirPapel()
    {
        var reader = new ScriptedTeamsReader(connected: true, refuseAll: true, status: 403, correlation: Correlation);
        var collector = new TeamsKnightCollector(new FixedTeamsTokens(GraphToken("Organization.Read.All"), TeamsToken()), reader);

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, TeamsConfig()));

        result.State.Should().Be(KnightSourceState.InsufficientPermission);
        var client = result.Capabilities.Single(c => c.Capability == KnightCapability.TeamsClientConfiguration);
        client.Detail.Should().StartWith("Autorização recusada no comando, com os dois tokens emitidos e a sessão do Teams estabelecida.")
            .And.Contain("Get-CsTeamsClientConfiguration").And.Contain("HTTP 403").And.Contain("correlação: " + Correlation);
        client.Diagnostic!.Kind.Should().Be(KnightFailureKind.Command);
        client.Diagnostic.HttpStatus.Should().Be(403);
        client.Diagnostic.ServiceRequestId.Should().Be(Correlation);
        client.Detail.Should().NotContain("Confira se a aplicação tem o papel");
    }

    // =====================================================================================================
    //  3. Identidade/recurso incompatível
    // =====================================================================================================

    [Fact]
    public async Task TokenDoTeamsEmitidoParaOGraph_EOutroLocatario_SaoIncompatibilidadesComprovadas()
    {
        var tokens = new TeamsAdminCredentials(TenantGuid,
            Jwt("https://graph.microsoft.com", tid: "00000000-0000-4000-8000-00000000ffff"),
            Jwt("https://graph.microsoft.com", wids: Array.Empty<string>()));

        var diagnosis = await KnightAccessDiagnostics.TeamsAsync(TeamsConfig(), tokens, graph: null, CancellationToken.None);

        diagnosis.Text.Should().Contain("token de Microsoft Graph incompatível: o token foi emitido por outro locatário")
            .And.Contain("foi emitido para outro recurso (Microsoft Graph)");
        diagnosis.Actions.Should().Contain(a => a.Contains("conferir o locatário e a aplicação configurados no conector"));
    }

    [Fact]
    public void ClaimsDoToken_SoValoresDeFormaFechada_NuncaOToken()
    {
        var token = Jwt("https://graph.microsoft.com", roles: new[] { "Organization.Read.All", "x y; DROP" },
            wids: new[] { DirectoryRoleNames.TeamsReader, "não-guid" }, extra: SecretMarker);

        var claims = AccessTokenClaims.TryRead(token)!;
        claims.Roles.Should().Equal("Organization.Read.All");
        claims.DirectoryRoleTemplateIds.Should().Equal(DirectoryRoleNames.TeamsReader);
        claims.ToString().Should().NotContain(SecretMarker).And.NotContain(token);
        AccessTokenClaims.TryRead("opaco-sem-pontos").Should().BeNull();
    }

    // =====================================================================================================
    //  4. Atribuições: encontrada, ausência comprovada, não verificável — e papel do token
    // =====================================================================================================

    [Fact]
    public async Task ConsultaDeAtribuicoesIndisponivel_NaoConcluiAusencia()
    {
        var graph = new RoutedGraph().Sp().Route("roleManagement/directory/roleAssignments", forbid: true);
        var tokens = new TeamsAdminCredentials(TenantGuid, GraphToken("Organization.Read.All"), TeamsToken(wids: new[] { DirectoryRoleNames.TeamsReader }));

        var diagnosis = await KnightAccessDiagnostics.TeamsAsync(TeamsConfig(), tokens, graph, CancellationToken.None);

        diagnosis.NotVerifiable.Should().Contain(u => u.Contains("atribuições ativas de papel da entidade de serviço (HTTP 403 · Authorization_RequestDenied)"));
        diagnosis.Text.Should().NotContain("não tem nenhuma atribuição", "falha de consulta nunca vira ausência");
        diagnosis.Verified.Should().Contain(v => v.Contains("declara o(s) papel(is) Leitor do Teams"));
        diagnosis.NotVerifiable.Should().Contain(u => u.Contains("se o papel de leitura presente basta para os comandos Get-Cs*"));
        diagnosis.Actions.Should().BeEmpty("com o papel no token e a consulta indisponível, nenhuma ação é demonstrada");
        graph.Urls.Should().Contain(u => u.Contains($"principalId eq '{SpObjectId}'"), "é a entidade de serviço da APLICAÇÃO que é consultada");
    }

    [Fact]
    public async Task SemPapelNoToken_ELeituraCompletaSemAtribuicao_AcaoDeAtribuirAEntidadeDeServico()
    {
        var graph = new RoutedGraph().Sp().Route("roleManagement/directory/roleAssignments", body: "[]");
        var tokens = new TeamsAdminCredentials(TenantGuid, GraphToken("Organization.Read.All"), TeamsToken());

        var diagnosis = await KnightAccessDiagnostics.TeamsAsync(TeamsConfig(), tokens, graph, CancellationToken.None);

        diagnosis.Verified.Should().Contain(v => v.Contains("não declara nenhum papel de diretório do Teams"))
            .And.Contain(v => v.Contains("não tem nenhuma atribuição ativa de papel de diretório (leitura completa)"))
            .And.Contain(v => v.Contains("Organization.Read.All (exigida pela documentação do módulo para todos os comandos) consta"));
        diagnosis.Actions.Should().ContainSingle(a => a.StartsWith("atribuir um papel de diretório do Teams à entidade de serviço da aplicação (não à conta do operador)"));
    }

    [Fact]
    public async Task AtribuicaoEncontradaMasAusenteDoToken_PropagacaoOuEscopo_NaoNovaAtribuicao()
    {
        var graph = new RoutedGraph().Sp().Route("roleManagement/directory/roleAssignments",
            body: $$"""[{"roleDefinitionId":"{{DirectoryRoleNames.TeamsAdministrator}}","directoryScopeId":"/administrativeUnits/au-1"}]""");
        var tokens = new TeamsAdminCredentials(TenantGuid, GraphToken("Organization.Read.All"), TeamsToken());

        var diagnosis = await KnightAccessDiagnostics.TeamsAsync(TeamsConfig(), tokens, graph, CancellationToken.None);

        diagnosis.Verified.Should().Contain(v => v.Contains("Administrador do Teams (escopo restrito)"));
        diagnosis.Actions.Should().ContainSingle(a => a.Contains("a atribuição existe, mas não chegou ao token desta execução"));
    }

    [Fact]
    public async Task PapelDeAdministracaoNoToken_RecusaNaoDecorreDaAusenciaDePapel()
    {
        var tokens = new TeamsAdminCredentials(TenantGuid, GraphToken("Organization.Read.All"),
            TeamsToken(wids: new[] { DirectoryRoleNames.TeamsAdministrator }));

        var diagnosis = await KnightAccessDiagnostics.TeamsAsync(TeamsConfig(), tokens, graph: null, CancellationToken.None);

        diagnosis.Verified.Should().Contain(v => v.Contains("a recusa não decorre da ausência de papel de diretório no token"));
        diagnosis.Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task PermissoesConfiguradasParaOApiDoTeams_DocumentadasComoCausaPossivel_AcaoDeRemover()
    {
        var tokens = new TeamsAdminCredentials(TenantGuid, GraphToken("Organization.Read.All"),
            TeamsToken(wids: new[] { DirectoryRoleNames.TeamsReader }, roles: new[] { "user_impersonation" }));

        var diagnosis = await KnightAccessDiagnostics.TeamsAsync(TeamsConfig(), tokens, graph: null, CancellationToken.None);

        diagnosis.Actions.Should().ContainSingle(a => a.Contains("remover da aplicação as permissões configuradas para «Skype and Teams Tenant Admin API»"));
    }

    [Fact]
    public async Task OrganizationReadAll_AusenteComDirectoryReadAll_NaoVerificavel_SemConfundirAsDuas()
    {
        var graph = new RoutedGraph().Sp().Route("roleManagement/directory/roleAssignments", body: "[]").Route("organization", body: "{}", single: true);
        var tokens = new TeamsAdminCredentials(TenantGuid, GraphToken("Directory.Read.All"), TeamsToken(wids: new[] { DirectoryRoleNames.TeamsAdministrator }));

        var diagnosis = await KnightAccessDiagnostics.TeamsAsync(TeamsConfig(), tokens, graph, CancellationToken.None);

        diagnosis.Verified.Should().Contain(v => v.Contains("Organization.Read.All, exigida pela documentação do módulo para todos os comandos, não consta"))
            .And.Contain(v => v.Contains("a leitura efetiva de /organization com o token do Graph foi aceita"));
        diagnosis.NotVerifiable.Should().Contain(u => u.Contains("se o módulo aceita Directory.Read.All no lugar de Organization.Read.All"));
        diagnosis.Actions.Should().NotContain(a => a.Contains("Organization.Read.All"), "Directory.Read.All presente: a falta efetiva não está demonstrada");

        var semNenhuma = await KnightAccessDiagnostics.TeamsAsync(TeamsConfig(),
            tokens with { GraphToken = GraphToken() }, graph: null, CancellationToken.None);
        semNenhuma.Actions.Should().Contain(a => a.StartsWith("conceder Organization.Read.All (aplicativo, Microsoft Graph)"));
    }

    // =====================================================================================================
    //  5. Fabric: recusado (com o que foi verificado) e resposta válida paginada
    // =====================================================================================================

    [Fact]
    public async Task Fabric_InsufficientScopes_PreservaCodigoEEtapa_EComprovaAsPermissoesIndevidasNoToken()
    {
        var rest = new MicrosoftRestClient(new HttpClient(new FabricHandler(forbidden: true)));
        var graph = new RoutedGraph().Sp().Route($"servicePrincipals/{SpObjectId}/transitiveMemberOf", body: """[{"id":"g1","securityEnabled":true}]""");
        var collector = new FabricKnightCollector(
            new FixedAppTokens(Jwt("https://api.fabric.microsoft.com", roles: new[] { "Tenant.Read.All" })), rest, graph: graph);

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, FabricConfig()));

        var cap = result.Capabilities.Single();
        cap.Outcome.Should().Be(KnightCapabilityOutcome.InsufficientPermission);
        cap.Detail.Should().StartWith("Microsoft Fabric (Power BI): Autorização recusada para esta leitura. HTTP 403 · código: InsufficientScopes · endpoint: /v1/admin/tenantsettings");
        cap.Detail.Should().Contain("token de API do Microsoft Fabric emitido para a aplicação e o locatário configurados, para o recurso esperado")
            .And.Contain("permissões de aplicativo do serviço Power BI/Fabric com consentimento (Tenant.Read.All)")
            .And.Contain("Ação necessária: remover essas permissões de aplicativo do Power BI/Fabric da aplicação")
            .And.Contain("pertence a 1 grupo(s) de segurança")
            .And.Contain("não se aplicam a esta autenticação de aplicativo");
        cap.Detail.Should().NotContain("desligad", "a configuração do portal não foi consultada").And.NotContain("Fabric-segredo");
        cap.Diagnostic!.ServiceRequestId.Should().Be(FabricHandler.RequestId);
    }

    [Fact]
    public async Task Fabric_SemGrupoDeSeguranca_AcaoComprovada_ESemGrupoConsultavel_NaoVerificavel()
    {
        var token = Jwt("https://api.fabric.microsoft.com");

        var semGrupo = await KnightAccessDiagnostics.FabricAsync(FabricConfig(), token,
            new RoutedGraph().Sp().Route($"servicePrincipals/{SpObjectId}/transitiveMemberOf", body: """[{"id":"g1","securityEnabled":false}]"""),
            CancellationToken.None);
        semGrupo.Verified.Should().Contain(v => v.Contains("não pertence a nenhum grupo de segurança (leitura completa"));
        semGrupo.Actions.Should().ContainSingle(a => a.StartsWith("incluir a entidade de serviço da aplicação num grupo de segurança"));
        semGrupo.Verified.Should().Contain(v => v.Contains("não traz permissões de aplicativo do Power BI/Fabric"));

        var indisponivel = await KnightAccessDiagnostics.FabricAsync(FabricConfig(), token,
            new RoutedGraph().Sp().Route($"servicePrincipals/{SpObjectId}/transitiveMemberOf", forbid: true), CancellationToken.None);
        indisponivel.NotVerifiable.Should().Contain(u => u.Contains("os grupos de segurança da entidade de serviço (HTTP 403"));
        indisponivel.Actions.Should().BeEmpty("sem leitura completa, a falta de grupo não está comprovada");
        indisponivel.NotVerifiable.Should().Contain(u => u.Contains("não afirma que esteja desligada"));
    }

    [Fact]
    public async Task Fabric_RespostaValidaPaginada_PorContinuationUri_TodasAsPaginasNoAdm()
    {
        var handler = new FabricHandler(forbidden: false);
        var collector = new FabricKnightCollector(new FixedAppTokens(Jwt("https://api.fabric.microsoft.com")), new MicrosoftRestClient(new HttpClient(handler)));

        var result = await collector.CollectAsync(new KnightCollectionContext(Tenant, FabricConfig()));

        result.Capabilities.Single().Outcome.Should().Be(KnightCapabilityOutcome.Collected);
        var settings = result.TenantConfigurationOrEmpty.Read<FabricTenantSetting>().Items;
        settings.Select(s => s.SettingName).Should().Equal("AdminApisIncludeDetailedMetadata", "DatamartTenant");
        settings.Single(s => s.SettingName == "DatamartTenant").EnabledSecurityGroups.Should().Equal("TestComputeGroup2");
        handler.Urls.Should().Equal(FabricKnightCollector.TenantSettingsUrl, FabricHandler.ContinuationUri);
    }

    // =====================================================================================================
    //  6. "Testar conexão": o diagnóstico chega à linha da fonte, e uma fonte não interrompe as outras
    // =====================================================================================================

    [Fact]
    public async Task TesteDeConexao_SegundoTokenDoTeamsRecusado_NaoCulpaOGraph_EAsDemaisFontesSeguem()
    {
        var connector = new KnightIdentityPostureConnector(
            new GuidSettingsProtector(),
            new RoutedGraph(),
            new TeamsTokenClient(new HttpClient(new TokenHandler(failScopeContains: "48ac35b8"))),
            new KnightIdentityPostureConnectorTests.FakeTeamsReader(connected: true, readOk: true),
            new KnightIdentityPostureConnectorTests.FakeExchangeTokens(),
            new KnightIdentityPostureConnectorTests.FakeExchangeReader(connected: true, readOk: true));

        var health = await connector.TestAsync(ConnectorConfig(), CancellationToken.None);

        var teams = health.Message!.Split('\n').Single(l => l.StartsWith("Microsoft Teams:"));
        teams.Should().Contain("recusou a emissão do token de Skype and Teams Tenant Admin API").And.Contain("AADSTS7000215");
        teams.Should().NotContain("pelo Microsoft Graph").And.NotContain("confira Organization.Read.All").And.NotContain(SecretMarker);
        health.Message.Should().Contain("Exchange Online: credencial aceita", "a falha do Teams não interrompe o teste do Exchange");
        health.Status.Should().Be(ConnectorStatus.Degraded);
    }

    [Fact]
    public async Task TesteDeConexao_LeituraDoTeamsAceita_NaoEhApresentadaComoValidacaoDosControles()
    {
        var connector = new KnightIdentityPostureConnector(
            new GuidSettingsProtector(),
            new RoutedGraph(),
            new FixedTeamsTokens(GraphToken(), TeamsToken()),
            new KnightIdentityPostureConnectorTests.FakeTeamsReader(connected: true, readOk: true),
            new KnightIdentityPostureConnectorTests.FakeExchangeTokens(),
            new KnightIdentityPostureConnectorTests.FakeExchangeReader(connected: true, readOk: true));

        var health = await connector.TestAsync(ConnectorConfig(), CancellationToken.None);

        health.Message!.Split('\n').Single(l => l.StartsWith("Microsoft Teams:"))
            .Should().Contain("não valida as outras cinco leituras nem os controles");
    }

    [Fact]
    public async Task TesteDeConexao_ComandoDoTeamsRecusado_TrazODiagnosticoDeAcesso()
    {
        var graph = new RoutedGraph().Sp().Route("roleManagement/directory/roleAssignments", body: "[]");
        var connector = new KnightIdentityPostureConnector(
            new GuidSettingsProtector(),
            graph,
            new FixedTeamsTokens(GraphToken("Organization.Read.All"), TeamsToken()),
            new ScriptedTeamsReader(connected: true, refuseAll: true, status: 403, correlation: Correlation),
            new KnightIdentityPostureConnectorTests.FakeExchangeTokens(),
            new KnightIdentityPostureConnectorTests.FakeExchangeReader(connected: true, readOk: true));

        var health = await connector.TestAsync(ConnectorConfig(), CancellationToken.None);

        var teams = health.Message!.Split('\n').Single(l => l.StartsWith("Microsoft Teams:"));
        teams.Should().Contain("os dois tokens foram emitidos e a conexão foi estabelecida")
            .And.Contain("Get-CsTeamsClientConfiguration").And.Contain("HTTP 403").And.Contain("correlação: " + Correlation)
            .And.Contain("Verificado: ").And.Contain("Ação necessária: atribuir um papel de diretório do Teams à entidade de serviço");
    }

    // ---- Duplas de teste ------------------------------------------------------------------------------

    private static KnightMicrosoftServiceConfiguration FabricConfig() => new(KnightSourceType.MicrosoftFabric, TenantGuid, AppGuid, "secret");

    private static ConnectorConfig ConnectorConfig() => new()
    {
        TenantId = Tenant, Provider = ConnectorProvider.Microsoft, Capability = ConnectorCapability.IdentityPosture,
        EncryptedSettings = "cifrado-sintetico",
    };

    private sealed class GuidSettingsProtector : IConnectorSecretProtector
    {
        public string Protect(string plaintext) => plaintext;

        public string Unprotect(string protectedText) =>
            JsonSerializer.Serialize(new { TenantId = TenantGuid, ClientId = AppGuid, ClientSecret = "secret" });
    }

    /// <summary>Endpoint de token sintético: emite JWT, ou recusa o escopo indicado com o corpo OAuth completo.</summary>
    internal sealed class TokenHandler(string failScopeContains) : HttpMessageHandler
    {
        public List<string> Scopes { get; } = new();
        public List<string> Urls { get; } = new();
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            Urls.Add(request.RequestUri!.ToString());
            Bodies.Add(body);
            var scope = Uri.UnescapeDataString(body.Split('&').Single(p => p.StartsWith("scope=")).Substring(6));
            Scopes.Add(scope);
            if (scope.Contains(failScopeContains, StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent($$"""
                        {"error":"invalid_client","error_description":"AADSTS7000215: Invalid client secret provided for app {{AppGuid}}. {{SecretMarker}}",
                         "error_codes":[7000215],"timestamp":"2026-10-27 13:16:31Z","trace_id":"{{Trace}}","correlation_id":"{{Correlation}}"}
                        """),
                };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { access_token = Jwt(scope.Replace("/.default", "")), token_type = "Bearer" })),
            };
        }
    }

    /// <summary>API do Fabric sintética: 403 InsufficientScopes, ou duas páginas encadeadas por continuationUri.</summary>
    private sealed class FabricHandler(bool forbidden) : HttpMessageHandler
    {
        internal const string RequestId = "0f0f0f0f-1111-4222-8333-444444444444";
        internal const string ContinuationUri = "https://api.fabric.microsoft.com/v1/admin/tenantsettings?continuationToken=p2";
        public List<string> Urls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Urls.Add(url);
            if (forbidden)
            {
                var denied = new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent($$"""{"requestId":"{{RequestId}}","errorCode":"InsufficientScopes","message":"Fabric-segredo-sintetico","isRetriable":false}"""),
                };
                denied.Headers.Add("RequestId", RequestId);
                return Task.FromResult(denied);
            }
            var body = url.Contains("continuationToken=p2", StringComparison.Ordinal)
                ? """{"value":[{"settingName":"DatamartTenant","title":"Create Datamarts (Preview)","enabled":true,"canSpecifySecurityGroups":true,"enabledSecurityGroups":[{"graphId":"1fecf19f-6e33-41b3-89fa-de8c821f3b79","name":"TestComputeGroup2"}],"tenantSettingGroup":"DatamartSettings"}]}"""
                : $$"""{"value":[{"settingName":"AdminApisIncludeDetailedMetadata","title":"Enhance admin APIs responses with detailed metadata","enabled":true,"canSpecifySecurityGroups":true,"tenantSettingGroup":"AdminApiSettings"}],"continuationUri":"{{ContinuationUri}}","continuationToken":"p2"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    /// <summary>Microsoft Graph sintético por prefixo de rota. Rota ausente = 404 (como o serviço).</summary>
    internal sealed class RoutedGraph : IEntraGraphClient
    {
        private readonly List<(string Prefix, string? Body, bool Forbid, bool Single)> _routes = new();
        public List<string> Urls { get; } = new();

        public RoutedGraph Route(string prefix, string? body = null, bool forbid = false, bool single = false)
        {
            _routes.Add((prefix, body, forbid, single));
            return this;
        }

        public RoutedGraph Sp() => Route($"servicePrincipals(appId='{AppGuid}')", body: $$"""{"id":"{{SpObjectId}}"}""", single: true);

        public Task<string> AcquireTokenAsync(IMicrosoftGraphCredentials config, CancellationToken ct) => Task.FromResult(GraphToken());

        private (string? Body, bool Forbid) Resolve(string url)
        {
            Urls.Add(url);
            var r = _routes.FirstOrDefault(x => url.StartsWith(x.Prefix, StringComparison.Ordinal));
            if (r.Prefix is null) throw new EntraGraphException(EntraGraphErrorKind.Unavailable, "rota sintética inexistente", 404, "Request_ResourceNotFound");
            if (r.Forbid) throw new EntraGraphException(EntraGraphErrorKind.InsufficientPermission, "graph retornou 403", 403, "Authorization_RequestDenied");
            return (r.Body, r.Forbid);
        }

        public async IAsyncEnumerable<JsonElement> GetPagedAsync(string token, IMicrosoftGraphCredentials config, string relativeUrl,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            var (body, _) = Resolve(relativeUrl);
            using var doc = JsonDocument.Parse(body ?? "[]");
            foreach (var e in doc.RootElement.EnumerateArray()) yield return e.Clone();
        }

        public Task<JsonElement> GetJsonAsync(string token, IMicrosoftGraphCredentials config, string relativeUrl, CancellationToken ct)
        {
            var (body, _) = Resolve(relativeUrl);
            using var doc = JsonDocument.Parse(body ?? "{}");
            return Task.FromResult(doc.RootElement.Clone());
        }
    }

    internal sealed class FixedTeamsTokens(string graph, string teams) : ITeamsTokenClient
    {
        public Task<TeamsAdminCredentials> AcquireAsync(IMicrosoftGraphCredentials credentials, CancellationToken ct = default) =>
            Task.FromResult(new TeamsAdminCredentials(credentials.AzureTenantId, graph, teams));
    }

    private sealed class FixedAppTokens(string token) : IMicrosoftAppTokenClient
    {
        public Task<string> AcquireAsync(IMicrosoftGraphCredentials credentials, string scope, CancellationToken ct = default) => Task.FromResult(token);
    }

    /// <summary>Saída do adaptador: conecta; com <c>refuseAll</c>, todas as leituras recusadas com status e correlação.</summary>
    internal sealed class ScriptedTeamsReader(bool connected, bool refuseAll = false, int? status = null, string? correlation = null) : ITeamsAdminReader
    {
        public int Calls { get; private set; }

        public Task<TeamsAdminOutput> ReadAsync(TeamsAdminCredentials credentials, CancellationToken ct = default)
        {
            Calls++;
            var reads = TeamsKnightCollector.Capabilities.Select(c => new
            {
                capability = c.ToString(),
                command = c == KnightCapability.TeamsClientConfiguration ? "Get-CsTeamsClientConfiguration" : "Get-" + c,
                ok = !refuseAll,
                items = Array.Empty<object>(),
                errorCategory = refuseAll ? "InsufficientPermission" : null,
                errorId = refuseAll ? "Forbidden,Microsoft.Teams.ConfigAPI.Cmdlets/HttpRequestException" : null,
                errorStatus = refuseAll ? status : null,
                errorCorrelationId = refuseAll ? correlation : null,
            });
            return Task.FromResult(PowerShellTeamsAdminReader.Parse(JsonSerializer.Serialize(new
            {
                runtime = new { powerShell = "7.4.7", module = "7.9.0", platform = "Unix" },
                connected,
                reads,
            })));
        }

        public Task<TeamsAdminOutput> CheckRuntimeAsync(CancellationToken ct = default) => ReadAsync(null!, ct);

        public async Task<TeamsAdminOutput> TestConnectionAsync(TeamsAdminCredentials credentials, CancellationToken ct = default)
        {
            var output = await ReadAsync(credentials, ct);
            return output with { Reads = output.Reads.Take(1).ToList() };
        }
    }
}
