using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace AegisScore.Infrastructure.Tests.Integration;

/// <summary>
/// [AEGIS-MVP-PRE-ADM-01] A jornada do produto atravessada pelo PIPELINE HTTP REAL, com JWT real e
/// PostgreSQL real: autenticar → selecionar ambiente → ler avaliação e afetados → criar plano → registrar
/// execução → validar → concluir/reabrir → publicar a avaliação EXATA → consultar/exportar a fotografia
/// congelada.
///
/// Por que esta classe existe: <c>RemediationJourneyTests</c> (SQLite) e <c>RemediationPostgresTests</c>
/// provam a SEMÂNTICA e a PERSISTÊNCIA, e ambas declaravam o mesmo limite — o pipeline HTTP e a autenticação
/// JWT não eram executados. Tudo entre o cliente e o serviço ficava sem regressão: roteamento, model binding,
/// emissão/validação do token, <c>FallbackPolicy</c>, <c>[Authorize(Roles=…)]</c>,
/// <c>TenantConsistencyMiddleware</c> e o Global Query Filter alimentado pela claim. Estes casos não repetem
/// a suíte unitária: cada um exercita um COMPORTAMENTO que só existe quando a requisição atravessa o host.
///
/// Esta cobertura foi desenhada para SOBREVIVER à troca da origem dos dados: quando o ADM por identidade
/// passar a alimentar a avaliação, o contrato HTTP verificado aqui continua o mesmo — muda apenas a fixture
/// da fronteira de coleta.
///
/// LIMITES declarados: a fonte de identidade é SINTÉTICA (fixture no lugar do coletor do Microsoft Graph);
/// isto NÃO comprova integração com um tenant Microsoft real. E validação HTTP não é validação de interface:
/// o navegador, o roteamento do Angular e o comportamento visual das telas não são exercitados aqui.
/// </summary>
public sealed class RemediationJourneyHttpTests : IClassFixture<AegisApiFixture>
{
    private const string Indicator = "AK-ENTRA-001";

    private readonly ITestOutputHelper _output;
    private readonly AegisApiHarness? _api;

    public RemediationJourneyHttpTests(AegisApiFixture fixture, ITestOutputHelper output)
    {
        // UMA instância do host e UM banco descartável para a classe inteira: subir a API e semear o catálogo
        // NIST a cada caso multiplicaria por sete o custo do job de CI sem provar nada a mais. O isolamento
        // entre os casos vem de cada um semear o PRÓPRIO ambiente (tenant + identidades).
        _api = fixture.Api;
        _output = output;
    }

    // ---- (1) Autenticação e tenancy: fail-closed em cada porta ---------------------------------------

    /// <summary>
    /// A porta de entrada. Sem token, com token forjado, sem <c>X-Tenant</c>, com <c>X-Tenant</c> malformado
    /// e com <c>X-Tenant</c> de OUTRO ambiente — cada um tem o seu desfecho, e nenhum deles entrega dado.
    /// A FallbackPolicy e o <c>TenantConsistencyMiddleware</c> só existem no pipeline HTTP: nenhum teste de
    /// serviço poderia provar isto.
    /// </summary>
    [Fact]
    public async Task Sessao_SemToken_Forjado_OuComTenantDivergente_NaoEntregaDado()
    {
        if (_api is null) return;   // AEGIS_TEST_PG ausente — pulado honestamente
        var alfa = await _api.SeedTenantAsync("Cliente Alfa");
        var beta = await _api.SeedTenantAsync("Cliente Beta");

        foreach (var rota in new[]
                 {
                     "/api/v1/remediation/action-plans",
                     "/api/v1/posture/snapshots",
                     "/api/v1/knight/assessments/latest",
                 })
        {
            using (var anonimo = _api.Anonymous())
                (await anonimo.GetAsync(rota)).StatusCode.Should().Be(
                    HttpStatusCode.Unauthorized, $"a rota {rota} não pode responder a quem não se autenticou");

            // Token SINTATICAMENTE plausível, mas assinado com outra chave: a validação do JwtBearer o recusa.
            using (var forjado = _api.As(ForgedToken(alfa.Manager.TenantId), alfa.Manager.TenantId))
                (await forjado.GetAsync(rota)).StatusCode.Should().Be(
                    HttpStatusCode.Unauthorized, $"a rota {rota} não pode aceitar um token de outra assinatura");

            using (var semHeader = _api.Anonymous())
            {
                semHeader.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", alfa.Manager.AccessToken);
                (await semHeader.GetAsync(rota)).StatusCode.Should().Be(
                    HttpStatusCode.BadRequest, "o X-Tenant é obrigatório na rota tenant-scoped");
            }

            using (var malformado = _api.Anonymous())
            {
                malformado.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", alfa.Manager.AccessToken);
                malformado.DefaultRequestHeaders.Add("X-Tenant", "nao-e-um-guid");
                (await malformado.GetAsync(rota)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            }

            // Token de Alfa + cabeçalho de Beta: tentativa de acesso cruzado, recusada ANTES do controller.
            using (var divergente = _api.As(alfa.Manager.AccessToken, beta.Id))
                (await divergente.GetAsync(rota)).StatusCode.Should().Be(
                    HttpStatusCode.Forbidden, "o tenant do token diverge do requisitado");
        }
    }

    // ---- (2) Papéis: quem lê e quem escreve -----------------------------------------------------------

    /// <summary>
    /// Analyst LÊ toda a evidência e NÃO muta nada; Manager e TenantAdmin executam as operações permitidas.
    /// O <c>[Authorize(Roles=…)]</c> depende da claim <c>role</c> emitida no login e do
    /// <c>RoleClaimType</c> configurado no handler — uma configuração que só é exercida por requisição real.
    /// </summary>
    [Fact]
    public async Task Analyst_Le_MasNaoMuta_EManagerETenantAdmin_Executam()
    {
        if (_api is null) return;
        var t0 = _api.OpenTimeWindow();
        var t = await _api.SeedTenantAsync("Cliente Papeis");
        await ConfigurarConectorEntraAsync(t);
        var origem = await ColetarAsync(t.Manager, semMfa: 3, em: t0.AddHours(1));

        using var analyst = _api.As(t.Analyst);
        using var manager = _api.As(t.Manager);
        using var admin = _api.As(t.Admin);

        // LEITURA — permitida ao Analyst em toda a superfície da jornada.
        (await analyst.GetAsync("/api/v1/remediation/action-plans")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await analyst.GetAsync($"/api/v1/knight/assessments/{origem}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await analyst.GetAsync(
            $"/api/v1/knight/assessments/{origem}/indicators/{Indicator}/affected")).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await analyst.GetAsync("/api/v1/posture/snapshots")).StatusCode.Should().Be(HttpStatusCode.OK);

        // MUTAÇÃO — recusada ao Analyst em cada verbo de escrita da jornada.
        var criacao = NovoPlano(origem, "Tentativa do Analyst");
        (await analyst.PostAsync("/api/v1/remediation/action-plans", criacao())).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "Analyst não cria ação");
        (await analyst.PostAsync("/api/v1/posture/snapshots",
            AegisApiHarness.JsonBody(new { type = "knight", source = (string?)null, runId = origem }))).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "Analyst não publica fotografia");

        // Manager cria; TenantAdmin edita a MESMA ação e publica — os dois papéis de escrita, sem matriz nova.
        var criado = await PostOkAsync(manager, "/api/v1/remediation/action-plans", criacao(), HttpStatusCode.Created);
        var planoId = criado.GetProperty("id").GetGuid();

        (await analyst.PutAsync($"/api/v1/remediation/action-plans/{planoId}",
            AegisApiHarness.JsonBody(new { expectedVersion = criado.GetProperty("version").GetInt32(), title = "Analyst" })))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "Analyst não edita ação");

        var editado = await PutOkAsync(admin, $"/api/v1/remediation/action-plans/{planoId}",
            AegisApiHarness.JsonBody(new
            {
                expectedVersion = criado.GetProperty("version").GetInt32(),
                responsiblePerson = "Equipe de Identidade",
            }));
        editado.GetProperty("responsiblePerson").GetString().Should().Be("Equipe de Identidade");

        var foto = await PostOkAsync(manager, "/api/v1/posture/snapshots",
            AegisApiHarness.JsonBody(new { type = "knight", runId = origem }), HttpStatusCode.Created);
        foto.GetProperty("summary").GetProperty("sourceRunId").GetGuid().Should().Be(origem);
    }

    // ---- (3) Jornada completa: criar → executar → validar → concluir ---------------------------------

    /// <summary>
    /// O caminho inteiro por HTTP, incluindo as duas recusas que sustentam a honestidade do produto: a tela
    /// desatualizada não sobrescreve (409 de versão) e o encerramento sem base é recusado com o motivo dito.
    /// </summary>
    [Fact]
    public async Task Jornada_DaColetaAoEncerramento_ComConflitoDeVersaoEEncerramentoSemBaseRecusados()
    {
        if (_api is null) return;
        var t0 = _api.OpenTimeWindow();
        var t = await _api.SeedTenantAsync("Cliente Jornada");
        await ConfigurarConectorEntraAsync(t);

        using var manager = _api.As(t.Manager);
        using var analyst = _api.As(t.Analyst);

        // (a) COLETA de origem — 3 contas privilegiadas sem MFA.
        var origem = await ColetarAsync(t.Manager, semMfa: 3, em: t0.AddHours(1));

        // (b) AFETADOS: a lista que sustenta o número, paginada no servidor.
        var afetados = await GetOkAsync(analyst,
            $"/api/v1/knight/assessments/{origem}/indicators/{Indicator}/affected?page=1&pageSize=2");
        afetados.GetProperty("affectedObjectCount").GetInt32().Should().Be(3);
        afetados.GetProperty("totalPreserved").GetInt32().Should().Be(3);
        afetados.GetProperty("items").GetArrayLength().Should()
            .Be(2, "a paginação acontece no servidor — o navegador não recebe a lista inteira");

        // (c) CRIAÇÃO da ação a partir daquele achado.
        var criado = await PostOkAsync(manager, "/api/v1/remediation/action-plans",
            NovoPlano(origem, "Registrar segundo fator nas contas administrativas")(), HttpStatusCode.Created);
        var planoId = criado.GetProperty("id").GetGuid();
        criado.GetProperty("originRunId").GetGuid().Should().Be(origem);
        criado.GetProperty("originAffectedCount").GetInt32().Should().Be(3);
        criado.GetProperty("originSourceType").GetString().Should().Be("MicrosoftEntraId");
        criado.GetProperty("originMode").GetString().Should().Be("Live");
        criado.GetProperty("status").GetString().Should().Be("Aberto");

        // Um segundo clique na MESMA origem abre a ação existente em vez de duplicar.
        var duplicado = await manager.PostAsync("/api/v1/remediation/action-plans",
            NovoPlano(origem, "Segundo clique")());
        duplicado.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonOf(duplicado)).GetProperty("existingActionPlanId").GetGuid().Should().Be(planoId);

        // (d) LEITURA por id — o mesmo registro, agora pela rota de detalhe.
        var lido = await GetOkAsync(analyst, $"/api/v1/remediation/action-plans/{planoId}");
        lido.GetProperty("id").GetGuid().Should().Be(planoId);
        var versao = lido.GetProperty("version").GetInt32();

        // (e) EDIÇÃO com a versão lida; a tela que ficou aberta com a versão anterior recebe 409.
        var editado = await PutOkAsync(manager, $"/api/v1/remediation/action-plans/{planoId}",
            AegisApiHarness.JsonBody(new
            {
                expectedVersion = versao,
                responsiblePerson = "Ana Souza",
                dueDate = Prazo(30),
            }));
        editado.GetProperty("responsiblePerson").GetString().Should().Be("Ana Souza");
        editado.GetProperty("version").GetInt32().Should().BeGreaterThan(versao);

        var telaVelha = await manager.PutAsync($"/api/v1/remediation/action-plans/{planoId}",
            AegisApiHarness.JsonBody(new { expectedVersion = versao, responsiblePerson = "Sobrescrito" }));
        telaVelha.StatusCode.Should().Be(HttpStatusCode.Conflict, "escrita com versão vencida não sobrescreve");
        (await GetOkAsync(analyst, $"/api/v1/remediation/action-plans/{planoId}"))
            .GetProperty("responsiblePerson").GetString().Should().Be("Ana Souza", "a escrita recusada não deixou rastro");

        // (f) ENCERRAR sem base é recusado — e o servidor DIZ o que falta.
        var atual = await GetOkAsync(manager, $"/api/v1/remediation/action-plans/{planoId}");
        atual.GetProperty("closureBlockedReason").GetString().Should().NotBeNullOrWhiteSpace();
        var semBase = await manager.PutAsync($"/api/v1/remediation/action-plans/{planoId}",
            AegisApiHarness.JsonBody(new { expectedVersion = atual.GetProperty("version").GetInt32(), status = "Concluido" }));
        semBase.StatusCode.Should().Be(HttpStatusCode.BadRequest, "concluir exige execução relatada e validação");

        // (g) EXECUÇÃO relatada — relógio controlado, para a ordem temporal ser determinística.
        _api.Clock.SetUtcNow(t0.AddHours(4));
        var executado = await PostOkAsync(manager, $"/api/v1/remediation/action-plans/{planoId}/execution",
            AegisApiHarness.JsonBody(new
            {
                expectedVersion = atual.GetProperty("version").GetInt32(),
                notes = "MFA resistente a phishing registrado nas tres contas.",
                evidenceReference = "CHAMADO-9001",
            }));
        executado.GetProperty("status").GetString().Should().Be("AguardandoValidacao");
        executado.GetProperty("latestValidation").ValueKind.Should()
            .Be(JsonValueKind.Null, "relatar execução não comprova correção");

        // (h) NOVA COLETA posterior ao trabalho relatado, e VALIDAÇÃO decidida pelo servidor.
        var evidencia = await ColetarAsync(t.Manager, semMfa: 0, em: t0.AddHours(5));
        _api.Clock.SetUtcNow(t0.AddHours(6));
        var validado = await PostOkAsync(manager, $"/api/v1/remediation/action-plans/{planoId}/validations",
            AegisApiHarness.JsonBody(new
            {
                expectedVersion = executado.GetProperty("version").GetInt32(),
                validationRunId = evidencia,
            }));

        var v = validado.GetProperty("latestValidation");
        v.GetProperty("outcome").GetString().Should().Be("ExposureCleared");
        v.GetProperty("validationRunId").GetGuid().Should().Be(evidencia);
        v.GetProperty("precedesReportedExecution").GetBoolean().Should()
            .BeFalse("a coleta é posterior à execução relatada");
        v.GetProperty("appliesToCurrentCycle").GetBoolean().Should().BeTrue();
        v.GetProperty("observedBefore").GetInt32().Should().Be(3);
        v.GetProperty("observedAfter").GetInt32().Should().Be(0);
        validado.GetProperty("originRunId").GetGuid().Should()
            .Be(origem, "validar não reescreve a avaliação de origem");

        // (i) ENCERRAMENTO agora permitido — a decisão continua sendo um ato explícito de gestão.
        validado.GetProperty("closureBlockedReason").ValueKind.Should().Be(JsonValueKind.Null);
        validado.GetProperty("allowedTransitions").EnumerateArray()
            .Select(x => x.GetString()).Should().Contain("Concluido");

        var concluido = await PutOkAsync(manager, $"/api/v1/remediation/action-plans/{planoId}",
            AegisApiHarness.JsonBody(new { expectedVersion = validado.GetProperty("version").GetInt32(), status = "Concluido" }));
        concluido.GetProperty("status").GetString().Should().Be("Concluido");
        concluido.GetProperty("isActive").GetBoolean().Should().BeFalse();
    }

    // ---- (4) Reabertura: o ciclo novo não herda a comprovação do anterior ----------------------------

    [Fact]
    public async Task Reabertura_NaoReaproveitaComprovacaoAntiga()
    {
        if (_api is null) return;
        var t0 = _api.OpenTimeWindow();
        var t = await _api.SeedTenantAsync("Cliente Reabertura");
        await ConfigurarConectorEntraAsync(t);
        using var manager = _api.As(t.Manager);

        var ciclo = await CicloConcluidoAsync(t, manager, t0);

        // Reabrir: novo ciclo. A validação anterior permanece no histórico e deixa de autorizar o encerramento.
        _api.Clock.SetUtcNow(t0.AddHours(8));
        var reaberto = await PutOkAsync(manager, $"/api/v1/remediation/action-plans/{ciclo.PlanoId}",
            AegisApiHarness.JsonBody(new { expectedVersion = ciclo.Versao, status = "EmAndamento" }));

        reaberto.GetProperty("status").GetString().Should().Be("EmAndamento");
        reaberto.GetProperty("applicableValidation").ValueKind.Should()
            .Be(JsonValueKind.Null, "a comprovação do ciclo anterior não fala pelo ciclo novo");
        reaberto.GetProperty("latestValidation").ValueKind.Should()
            .NotBe(JsonValueKind.Null, "ela continua no histórico — reabrir não apaga o que foi registrado");
        reaberto.GetProperty("latestValidation").GetProperty("appliesToCurrentCycle").GetBoolean().Should().BeFalse();
        reaberto.GetProperty("closureBlockedReason").GetString().Should().NotBeNullOrWhiteSpace();
        reaberto.GetProperty("allowedTransitions").EnumerateArray()
            .Select(x => x.GetString()).Should().NotContain("Concluido");

        var atalho = await manager.PutAsync($"/api/v1/remediation/action-plans/{ciclo.PlanoId}",
            AegisApiHarness.JsonBody(new { expectedVersion = reaberto.GetProperty("version").GetInt32(), status = "Concluido" }));
        atalho.StatusCode.Should().Be(HttpStatusCode.BadRequest, "encerrar de novo exige executar e comprovar de novo");
    }

    // ---- (5) Publicação exata + fotografia congelada --------------------------------------------------

    /// <summary>
    /// Publicar pedindo um <c>runId</c> publica AQUELA avaliação — sem troca silenciosa pela mais recente. E o
    /// que foi publicado não se mexe: alterar o plano vivo depois não altera a fotografia, a reexportação
    /// devolve o mesmo arquivo e a verificação de integridade (hash) continua aprovando.
    /// </summary>
    [Fact]
    public async Task Publicacao_UsaARunPedida_EAFotografiaPermaneceEstavelDepoisDeAlterarOPlanoVivo()
    {
        if (_api is null) return;
        var t0 = _api.OpenTimeWindow();
        var t = await _api.SeedTenantAsync("Cliente Fotografia");
        await ConfigurarConectorEntraAsync(t);
        using var manager = _api.As(t.Manager);
        using var analyst = _api.As(t.Analyst);

        var ciclo = await CicloConcluidoAsync(t, manager, t0);

        // Uma coleta MAIS RECENTE existe (a evidência da validação). Publicar a avaliação de ORIGEM tem de
        // devolver a de origem — receber a mais recente por publicar a que está aberta na tela seria a
        // substituição silenciosa que o produto se recusa a fazer.
        var publicado = await PostOkAsync(manager, "/api/v1/posture/snapshots",
            AegisApiHarness.JsonBody(new { type = "knight", runId = ciclo.OrigemRunId }), HttpStatusCode.Created);

        var fotoId = publicado.GetProperty("summary").GetProperty("id").GetGuid();
        publicado.GetProperty("summary").GetProperty("sourceRunId").GetGuid().Should().Be(ciclo.OrigemRunId);
        publicado.GetProperty("summary").GetProperty("clientName").GetString().Should().Be(t.Label);

        var hashPublicado = publicado.GetProperty("summary").GetProperty("contentHash").GetString();
        hashPublicado.Should().NotBeNullOrWhiteSpace();

        var acoesCongeladas = publicado.GetProperty("actionItems");
        acoesCongeladas.GetArrayLength().Should().BeGreaterThan(0);
        var acao = acoesCongeladas.EnumerateArray().Single(a => a.GetProperty("actionPlanId").GetGuid() == ciclo.PlanoId);
        acao.GetProperty("status").GetString().Should().Be("Concluido");
        acao.GetProperty("responsiblePerson").GetString().Should().Be("Ana Souza");
        acao.GetProperty("validationAppliesToCurrentCycle").GetBoolean().Should().BeTrue();

        // Exportação ANTES de mexer no plano vivo.
        var (csvAntes, dispAntes) = await ExportarAsync(analyst, fotoId, "csv");
        dispAntes.Should().NotBeNullOrWhiteSpace("o nome do arquivo vem do servidor");

        // O plano VIVO muda depois da publicação: reabre e troca o responsável.
        _api.Clock.SetUtcNow(t0.AddHours(9));
        var reaberto = await PutOkAsync(manager, $"/api/v1/remediation/action-plans/{ciclo.PlanoId}",
            AegisApiHarness.JsonBody(new { expectedVersion = ciclo.Versao, status = "EmAndamento" }));
        await PutOkAsync(manager, $"/api/v1/remediation/action-plans/{ciclo.PlanoId}",
            AegisApiHarness.JsonBody(new
            {
                expectedVersion = reaberto.GetProperty("version").GetInt32(),
                responsiblePerson = "Outra Pessoa",
            }));

        // A fotografia publicada NÃO acompanha: nem o estado, nem o responsável, nem o hash.
        var relido = await GetOkAsync(analyst, $"/api/v1/posture/snapshots/{fotoId}");
        relido.GetProperty("summary").GetProperty("contentHash").GetString().Should().Be(hashPublicado);
        relido.GetProperty("summary").GetProperty("sourceRunId").GetGuid().Should().Be(ciclo.OrigemRunId);
        var acaoRelida = relido.GetProperty("actionItems").EnumerateArray()
            .Single(a => a.GetProperty("actionPlanId").GetGuid() == ciclo.PlanoId);
        acaoRelida.GetProperty("status").GetString().Should().Be("Concluido");
        acaoRelida.GetProperty("responsiblePerson").GetString().Should().Be("Ana Souza");

        // Reexportar depois da alteração: o ContentHash é REVERIFICADO antes de gerar (divergência viraria
        // 409 e bloquearia a exportação), e o arquivo sai idêntico.
        var (csvDepois, _) = await ExportarAsync(analyst, fotoId, "csv");
        csvDepois.Should().Equal(csvAntes, "a fotografia é append-only: o relatório de ontem é o de ontem");

        // A ação VIVA, por sua vez, refletiu a mudança — as duas leituras são distintas de propósito.
        (await GetOkAsync(analyst, $"/api/v1/remediation/action-plans/{ciclo.PlanoId}"))
            .GetProperty("responsiblePerson").GetString().Should().Be("Outra Pessoa");
    }

    // ---- (6) Isolamento entre ambientes ---------------------------------------------------------------

    /// <summary>
    /// Com token legítimo do PRÓPRIO ambiente, os identificadores do outro simplesmente não existem: avaliação,
    /// afetados, ação e fotografia respondem 404 — nunca 403, que confirmaria a existência do registro — e
    /// nenhuma escrita atravessa.
    /// </summary>
    [Fact]
    public async Task AcessoCruzado_NaoRevelaNemAltera_DadosDeOutroAmbiente()
    {
        if (_api is null) return;
        var t0 = _api.OpenTimeWindow();
        var alfa = await _api.SeedTenantAsync("Cliente Alfa Isolado");
        var beta = await _api.SeedTenantAsync("Cliente Beta Isolado");
        await ConfigurarConectorEntraAsync(alfa);

        using var alfaManager = _api.As(alfa.Manager);
        using var betaManager = _api.As(beta.Manager);

        var origem = await ColetarAsync(alfa.Manager, semMfa: 3, em: t0.AddHours(1));
        var plano = (await PostOkAsync(alfaManager, "/api/v1/remediation/action-plans",
            NovoPlano(origem, "Ação do Alfa")(), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var foto = (await PostOkAsync(alfaManager, "/api/v1/posture/snapshots",
            AegisApiHarness.JsonBody(new { type = "knight", runId = origem }), HttpStatusCode.Created))
            .GetProperty("summary").GetProperty("id").GetGuid();

        // LEITURA cruzada — tudo indistinguível de inexistente.
        (await betaManager.GetAsync($"/api/v1/knight/assessments/{origem}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await betaManager.GetAsync(
            $"/api/v1/knight/assessments/{origem}/indicators/{Indicator}/affected")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await betaManager.GetAsync($"/api/v1/remediation/action-plans/{plano}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await betaManager.GetAsync($"/api/v1/posture/snapshots/{foto}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await betaManager.GetAsync($"/api/v1/posture/snapshots/{foto}/export?format=csv")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        (await GetOkAsync(betaManager, "/api/v1/remediation/action-plans")).GetArrayLength().Should()
            .Be(0, "a lista do outro ambiente não vaza pelo filtro global");

        // ESCRITA cruzada — nem altera, nem cria a partir da avaliação alheia.
        (await betaManager.PutAsync($"/api/v1/remediation/action-plans/{plano}",
            AegisApiHarness.JsonBody(new { expectedVersion = 1, responsiblePerson = "Invasor" }))).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await betaManager.PostAsync($"/api/v1/remediation/action-plans/{plano}/execution",
            AegisApiHarness.JsonBody(new { expectedVersion = 1, notes = "Invasor" }))).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await betaManager.PostAsync("/api/v1/remediation/action-plans",
            NovoPlano(origem, "Ação nascida de avaliação alheia")())).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "a avaliação de outro ambiente não origina ação aqui");

        // E o registro do Alfa continua intacto.
        (await GetOkAsync(alfaManager, $"/api/v1/remediation/action-plans/{plano}"))
            .GetProperty("responsiblePerson").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // ---- (7) Demonstração × coleta real ---------------------------------------------------------------

    /// <summary>
    /// O MESMO indicador na demonstração e na coleta real são DOIS problemas. Tratá-los como um faria a ação
    /// de treinamento bloquear a ação real — e faria a lista apresentar trabalho de demonstração ao lado de
    /// trabalho de verdade, com a mesma aparência.
    /// </summary>
    [Fact]
    public async Task Demonstracao_E_ColetaReal_PermanecemSeparadas()
    {
        if (_api is null) return;
        var t0 = _api.OpenTimeWindow();
        var t = await _api.SeedTenantAsync("Cliente Separacao");
        await ConfigurarConectorEntraAsync(t);
        using var manager = _api.As(t.Manager);

        var demo = (await PostOkAsync(manager, "/api/v1/knight/assessments/demo", null))
            .GetProperty("id").GetGuid();
        var real = await ColetarAsync(t.Manager, semMfa: 3, em: t0.AddHours(1));

        var acaoDemo = await PostOkAsync(manager, "/api/v1/remediation/action-plans",
            NovoPlano(demo, "Treinamento sobre o achado")(), HttpStatusCode.Created);
        acaoDemo.GetProperty("originMode").GetString().Should().Be("Demo");
        acaoDemo.GetProperty("originSourceType").GetString().Should().Be("Demo");

        // MESMO indicador, coleta REAL: não é duplicidade — é outro problema, e é aceito.
        var acaoReal = await PostOkAsync(manager, "/api/v1/remediation/action-plans",
            NovoPlano(real, "Corrigir no diretório")(), HttpStatusCode.Created);
        acaoReal.GetProperty("originMode").GetString().Should().Be("Live");
        acaoReal.GetProperty("id").GetGuid().Should().NotBe(acaoDemo.GetProperty("id").GetGuid());

        // A tela recorta por procedência: cada visão mostra só o trabalho que lhe pertence.
        var soReal = await GetOkAsync(manager, "/api/v1/remediation/action-plans?mode=Live");
        soReal.EnumerateArray().Select(a => a.GetProperty("id").GetGuid())
            .Should().BeEquivalentTo(new[] { acaoReal.GetProperty("id").GetGuid() });

        var soDemo = await GetOkAsync(manager, "/api/v1/remediation/action-plans?sourceType=Demo");
        soDemo.EnumerateArray().Select(a => a.GetProperty("id").GetGuid())
            .Should().BeEquivalentTo(new[] { acaoDemo.GetProperty("id").GetGuid() });
    }

    // ---- Apoio ---------------------------------------------------------------------------------------

    /// <summary>Ciclo completo até "Concluído" — a base reutilizada pelos casos de reabertura e fotografia.</summary>
    private async Task<CicloConcluido> CicloConcluidoAsync(SeededTenant t, HttpClient manager, DateTimeOffset t0)
    {
        var api = _api!;   // só é chamado depois do early-return dos casos, quando o harness existe
        var origem = await ColetarAsync(t.Manager, semMfa: 3, em: t0.AddHours(1));

        var criado = await PostOkAsync(manager, "/api/v1/remediation/action-plans",
            NovoPlano(origem, "Registrar segundo fator nas contas administrativas")(), HttpStatusCode.Created);
        var planoId = criado.GetProperty("id").GetGuid();

        var editado = await PutOkAsync(manager, $"/api/v1/remediation/action-plans/{planoId}",
            AegisApiHarness.JsonBody(new
            {
                expectedVersion = criado.GetProperty("version").GetInt32(),
                responsiblePerson = "Ana Souza",
                dueDate = Prazo(30),
            }));

        api.Clock.SetUtcNow(t0.AddHours(4));
        var executado = await PostOkAsync(manager, $"/api/v1/remediation/action-plans/{planoId}/execution",
            AegisApiHarness.JsonBody(new
            {
                expectedVersion = editado.GetProperty("version").GetInt32(),
                notes = "MFA registrado nas tres contas.",
                evidenceReference = "CHAMADO-9001",
            }));

        var evidencia = await ColetarAsync(t.Manager, semMfa: 0, em: t0.AddHours(5));
        api.Clock.SetUtcNow(t0.AddHours(6));
        var validado = await PostOkAsync(manager, $"/api/v1/remediation/action-plans/{planoId}/validations",
            AegisApiHarness.JsonBody(new
            {
                expectedVersion = executado.GetProperty("version").GetInt32(),
                validationRunId = evidencia,
            }));
        validado.GetProperty("latestValidation").GetProperty("outcome").GetString().Should().Be("ExposureCleared");

        api.Clock.SetUtcNow(t0.AddHours(7));
        var concluido = await PutOkAsync(manager, $"/api/v1/remediation/action-plans/{planoId}",
            AegisApiHarness.JsonBody(new { expectedVersion = validado.GetProperty("version").GetInt32(), status = "Concluido" }));
        concluido.GetProperty("status").GetString().Should().Be("Concluido");

        return new CicloConcluido(origem, evidencia, planoId, concluido.GetProperty("version").GetInt32());
    }

    private sealed record CicloConcluido(Guid OrigemRunId, Guid EvidenciaRunId, Guid PlanoId, int Versao);

    /// <summary>
    /// Configura o conector Microsoft/IdentityPosture pelo endpoint REAL (TenantAdmin), com credenciais
    /// SINTÉTICAS. O segredo é cifrado no servidor pela Data Protection e decifrado na coleta — o caminho de
    /// configuração é o de produção; só a fronteira de rede é que é fixture.
    /// </summary>
    private async Task ConfigurarConectorEntraAsync(SeededTenant t)
    {
        using var admin = _api!.As(t.Admin);
        var settings = JsonSerializer.Serialize(new
        {
            tenantId = Guid.NewGuid().ToString(),
            clientId = Guid.NewGuid().ToString(),
            clientSecret = "segredo-sintetico-" + Guid.NewGuid().ToString("N"),
        });

        // Provider/Capability/AuthType viajam como ORDINAL: a API não instala converter de enum global, e
        // este contrato de escrita usa os tipos do domínio (Microsoft=0, IdentityPosture=10, OAuth=0).
        var response = await admin.PostAsync("/api/v1/tenants/connectors", AegisApiHarness.JsonBody(new
        {
            provider = 0,
            capability = 10,
            displayName = "Diretório sintético de validação",
            authType = 0,
            settings,
            syncIntervalMinutes = 360,
        }));

        response.IsSuccessStatusCode.Should()
            .BeTrue($"configurar o conector é pré-requisito da coleta real: {(int)response.StatusCode} " +
                    $"{await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>
    /// Dispara uma coleta da fonte REAL (Entra) com o cenário roteirizado e devolve o <c>runId</c>. Toda a
    /// cadeia — Evidence Fabric, avaliação determinística, persistência — é a de produção.
    /// </summary>
    private async Task<Guid> ColetarAsync(SeededUser quem, int semMfa, DateTimeOffset em)
    {
        _api!.Entra.Observe(privilegedTotal: 12, withoutMfa: semMfa, collectedAt: em);
        using var client = _api.As(quem);
        var assessment = await PostOkAsync(client, "/api/v1/knight/assessments/run/entra", null);

        assessment.GetProperty("sourceType").GetString().Should().Be("MicrosoftEntraId");
        assessment.GetProperty("mode").GetString().Should().Be("Live");
        return assessment.GetProperty("id").GetGuid();
    }

    private static Func<HttpContent> NovoPlano(Guid runId, string titulo) => () =>
        AegisApiHarness.JsonBody(new
        {
            runId,
            indicatorId = Indicator,
            title = titulo,
            proposedAction = "Registrar método resistente a phishing.",
            responsibleArea = "TI",
            dueDate = Prazo(45),
        });

    /// <summary>Prazo relativo ao HOJE real: uma data literal de calendário venceria sozinha com o tempo.</summary>
    private static string Prazo(int dias) =>
        DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(dias)).ToString("yyyy-MM-dd");

    /// <summary>Token com estrutura válida e assinatura ALHEIA — o que um atacante teria sem a chave.</summary>
    private static string ForgedToken(Guid tenantId)
    {
        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var chaveAlheia = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: "aegis-score",
            audience: "aegis-score",
            claims: new[]
            {
                new System.Security.Claims.Claim("sub", Guid.NewGuid().ToString()),
                new System.Security.Claims.Claim("tenant_id", tenantId.ToString()),
                new System.Security.Claims.Claim("role", "TenantAdmin"),
            },
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new Microsoft.IdentityModel.Tokens.SigningCredentials(
                chaveAlheia, Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256));
        return handler.WriteToken(token);
    }

    private async Task<(byte[] Content, string? Disposition)> ExportarAsync(
        HttpClient client, Guid snapshotId, string formato)
    {
        using var response = await client.GetAsync($"/api/v1/posture/snapshots/{snapshotId}/export?format={formato}");
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a exportação reverifica o hash antes de gerar — um 409 aqui significaria fotografia adulterada");
        return (await response.Content.ReadAsByteArrayAsync(),
            response.Content.Headers.ContentDisposition?.ToString());
    }

    private async Task<JsonElement> GetOkAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        await EnsureAsync(response, HttpStatusCode.OK, "GET " + url);
        return await JsonOf(response);
    }

    private async Task<JsonElement> PostOkAsync(
        HttpClient client, string url, HttpContent? body, HttpStatusCode esperado = HttpStatusCode.OK)
    {
        using var response = await client.PostAsync(url, body);
        await EnsureAsync(response, esperado, "POST " + url);
        return await JsonOf(response);
    }

    private async Task<JsonElement> PutOkAsync(HttpClient client, string url, HttpContent body)
    {
        using var response = await client.PutAsync(url, body);
        await EnsureAsync(response, HttpStatusCode.OK, "PUT " + url);
        return await JsonOf(response);
    }

    /// <summary>Falha com o CORPO da resposta: um 400 mudo custaria uma rodada inteira de CI para diagnosticar.</summary>
    private async Task EnsureAsync(HttpResponseMessage response, HttpStatusCode esperado, string rotulo)
    {
        if (response.StatusCode == esperado) return;
        var corpo = await response.Content.ReadAsStringAsync();
        _output.WriteLine($"{rotulo} -> {(int)response.StatusCode}: {corpo}");
        response.StatusCode.Should().Be(esperado, $"{rotulo} respondeu {(int)response.StatusCode}: {corpo}");
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }
}
