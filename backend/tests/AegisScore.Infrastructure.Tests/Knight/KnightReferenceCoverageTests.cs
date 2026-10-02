using System;
using System.Collections.Generic;
using System.Linq;
using AegisScore.Application.Knight;
using AegisScore.Application.Knight.Catalog;
using AegisScore.Application.Knight.Configuration;
using AegisScore.Application.Knight.Reference;
using AegisScore.Application.Posture;
using AegisScore.Domain;
using FluentAssertions;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-01] Cobertura de IMPLEMENTAÇÃO do catálogo de referência: integral e parcial separadas,
/// nada escondido, nada declarado como implementado sem regra no fluxo ativo e coleta produzida por conector real.
/// </summary>
public sealed class KnightReferenceCoverageTests
{
    [Fact]
    public void CatalogoDeReferencia_FixadoNumCommit_ComChavesUnicas()
    {
        KnightReferenceCatalog.ReferenceCommit.Should().MatchRegex("^[0-9a-f]{40}$");
        KnightReferenceCatalog.Controls.Should().HaveCount(457);
        KnightReferenceCatalog.Controls.Select(c => c.Key).Should().OnlyHaveUniqueItems();
        KnightReferenceCatalog.Controls.Should().OnlyContain(c => c.Service != KnightService.Unspecified && !string.IsNullOrWhiteSpace(c.Title));
    }

    [Fact]
    public void Cobertura_SeparaIntegralDeParcial_ENaoSomaAsDuasComoCompleta()
    {
        var t = KnightReferenceCatalog.Coverage().Total;
        (t.Implemented + t.Partial + t.Pending + t.ManualOnly + t.RequiresAccess + t.ApiLimitation + t.PreviewOnly).Should().Be(t.Total);
        t.FullPercent.Should().Be(Math.Round(100.0 * t.Implemented / t.Total, 1, MidpointRounding.AwayFromZero));
        t.PartialPercent.Should().Be(Math.Round(100.0 * t.Partial / t.Total, 1, MidpointRounding.AwayFromZero));
        t.AnyAutomatedPercent.Should().BeGreaterThan(t.FullPercent, "há controles parciais, e eles não entram na cobertura integral");
    }

    [Fact]
    public void EntraId_TemCadaControleDeReferenciaClassificado_ENadaAlemDisso()
    {
        var entra = KnightReferenceCatalog.Coverage().ByPlatform.Single(p => p.Key == nameof(KnightPlatform.EntraId));
        entra.Total.Should().Be(83);
        // Números TRAVADOS: mudar a classificação exige revisar este teste — e explicar por quê no PR.
        // A revisão de convidados (CIS-M365-7.0.0:5.3.2) é PARCIAL: a avaliação comprova os convidados de todos os
        // grupos do Microsoft 365, que é o alcance da própria configuração, e a diferença está declarada no vínculo.
        entra.Implemented.Should().Be(55);
        // [AEGIS-KNIGHT-COVERAGE-04] +1 parcial: a proteção de token (5.2.2.16), antes declarada sem leitura, é avaliada por
        // AK-ENTRA-070 (secureSignInSession existe na v1.0). As 17 restantes separadas entre "só em preview" (a leitura existe
        // na beta) e "sem método" (não existe nem na beta) — conferido nos metadados publicados do Microsoft Graph.
        entra.Partial.Should().Be(9);
        entra.PreviewOnly.Should().Be(7);
        entra.ApiLimitation.Should().Be(10);
        entra.ManualOnly.Should().Be(2);
        entra.Pending.Should().Be(0);
        entra.RequiresAccess.Should().Be(0);
    }

    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-02] As 17 referências de Microsoft Teams, uma a uma. Números TRAVADOS: mudar a
    /// classificação exige revisar este teste — e explicar por quê no PR.
    /// </summary>
    [Fact]
    public void Teams_TemAs17ReferenciasClassificadas_ComACoberturaQueOMetodoOperacionalSustenta()
    {
        var coverage = KnightReferenceCatalog.Coverage();
        var teams = coverage.ByService.Single(s => s.Key == nameof(KnightService.Teams));

        teams.Total.Should().Be(17);
        teams.Implemented.Should().Be(15);
        teams.Partial.Should().Be(1);
        teams.Pending.Should().Be(0);
        teams.ApiLimitation.Should().Be(0);
        teams.ManualOnly.Should().Be(0);
        teams.RequiresAccess.Should().Be(1,
            "8.4.1 não é avaliado: a leitura que diria qual modelo governa os aplicativos não é suportada com "
            + "autenticação de aplicativo, que é a forma de acesso desta coleta");

        var parciais = coverage.Controls
            .Where(c => c.Control.Service == KnightService.Teams && c.Disposition == KnightReferenceDisposition.Partial)
            .ToList();
        parciais.Select(p => p.Control.Section).Should().BeEquivalentTo(new[] { "8.6.1" });
        parciais.Single(p => p.Control.Section == "8.6.1").Note.Should().Contain("Defender para Office 365");

        // [Revisão dirigida] 8.4.1 saiu de PARCIAL. A condição de aplicabilidade que o mantinha como parcial foi
        // construída sobre Get-AllM365TeamsApps — comando que a Microsoft lista nominalmente entre os NÃO
        // SUPORTADOS com autenticação de aplicativo. A condição nunca rodaria no fluxo real, e contar o controle
        // como “parcialmente avaliado” declararia uma cobertura que o método operacional não sustenta.
        // A disposição real é “exige acesso que o conector não tem”, e a nota nomeia a causa — não uma permissão.
        var apps = coverage.Controls.Single(c => c.Control.Section == "8.4.1" && c.Control.Service == KnightService.Teams);
        apps.Disposition.Should().Be(KnightReferenceDisposition.RequiresAccess);
        apps.IndicatorIds.Should().BeEmpty("um controle que nunca conclui não pode figurar como quem avalia a referência");
        apps.Note.Should().Contain("NÃO SUPORTADOS com autenticação").And.Contain("Não é falta de permissão");

        // 8.5.3 é equivalência INTEGRAL: o critério avaliado é o da referência. O vínculo registra que o AEGIS
        // também aceita o valor estritamente mais restritivo, e que “pessoas convidadas” NÃO é aceito.
        var lobby = coverage.Controls.Single(c => c.Control.Section == "8.5.3" && c.Control.Service == KnightService.Teams);
        lobby.Disposition.Should().Be(KnightReferenceDisposition.Implemented);
        lobby.Note.Should().Contain("mais restritivo").And.Contain("InvitedUsers");

        // Cada referência de Teams AVALIADA é citada por um — e só um — controle do KNIGHT. A do 8.4.1 não é
        // citada por nenhum, justamente porque não é avaliada (a asserção dedicada acima exige que fique vazia).
        foreach (var c in coverage.Controls
                     .Where(c => c.Control.Service == KnightService.Teams && c.Control.Section != "8.4.1"))
            c.IndicatorIds.Should().ContainSingle(id => id.StartsWith("AK-TEAMS-"), c.Control.Key);
    }

    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-02] O bloco do Teams NÃO pode ser apresentado como "Microsoft 365 coberto": os
    /// demais serviços continuam pendentes, com o motivo, e isso é o que a plataforma reporta.
    /// </summary>
    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-03] As 17 referências de Exchange Online, classificadas pelo que o método
    /// operacional REALMENTE sustenta.
    ///
    /// Diferente do bloco do Teams, aqui não há referência barrada pela forma de acesso: todos os comandos que os
    /// critérios exigem são executáveis com a autenticação de aplicativo deste conector. O que EXISTE de
    /// limitação é de outra natureza e vive na execução, não na cobertura — enumeração com teto e leitura que
    /// pode ser recusada por papel —, e ela aparece como "não avaliado" na coleta do cliente, nunca como
    /// cobertura de implementação a menos.
    /// </summary>
    [Fact]
    public void ExchangeOnline_TemAs17ReferenciasImplementadas_SemDependerDeAcessoQueOConectorNaoTem()
    {
        var coverage = KnightReferenceCatalog.Coverage();
        var exo = coverage.ByService.Single(s => s.Key == nameof(KnightService.ExchangeOnline));

        exo.Total.Should().Be(17);
        exo.Implemented.Should().Be(17);
        exo.Partial.Should().Be(0);
        exo.Pending.Should().Be(0);
        exo.ApiLimitation.Should().Be(0);
        exo.ManualOnly.Should().Be(0);
        exo.RequiresAccess.Should().Be(0,
            "os comandos que estes critérios exigem funcionam com a autenticação de aplicativo deste conector");

        // As seções são exatamente as 17 da referência — nem uma a mais inventada, nem uma a menos esquecida.
        coverage.Controls
            .Where(c => c.Control.Service == KnightService.ExchangeOnline)
            .Select(c => c.Control.Section)
            .Should().BeEquivalentTo(new[]
            {
                "1.2.2", "1.3.3", "1.3.6", "1.3.9",
                "6.1.1", "6.1.2", "6.1.3",
                "6.2.1", "6.2.2", "6.2.3",
                "6.3.1", "6.3.2",
                "6.5.1", "6.5.2", "6.5.3", "6.5.4", "6.5.5",
            });

        // Cada referência é citada por um — e só um — controle do KNIGHT.
        foreach (var c in coverage.Controls.Where(c => c.Control.Service == KnightService.ExchangeOnline))
        {
            c.IndicatorIds.Should().ContainSingle(c.Control.Key);
            c.IndicatorIds.Single().Should().StartWith("AK-EXO-");
        }
    }

    /// <summary>
    /// Os controles do Exchange Online só consomem capacidades que o COLETOR REAL produz, declaram o serviço e a
    /// fonte certos e citam a referência que avaliam. Uma regra escrita sobre capacidade que ninguém produz nunca
    /// rodaria fora do teste — e não pode contar como cobertura.
    /// </summary>
    [Fact]
    public void ControlesDoExchange_SoConsomemCapacidadesDoColetorReal()
    {
        ExchangeConfigurationControls.Definitions.Should().HaveCount(17);
        foreach (var d in ExchangeConfigurationControls.Definitions)
        {
            KnightCollectorCapabilities.IsActive(d).Should().BeTrue(d.Id);
            d.Service.Should().Be(KnightService.ExchangeOnline);
            d.Sources.Should().BeEquivalentTo(new[] { KnightSourceType.MicrosoftExchangeOnline },
                $"{d.Id} lê a configuração do Exchange Online — aplicá-lo a outra fonte o deixaria não avaliado para sempre");
            d.References.Should().ContainSingle(d.Id);
        }
    }

    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-04] As 89 referências do Microsoft 365, serviço a serviço. Números TRAVADOS: mudar a
    /// classificação exige revisar este teste — e explicar por quê no PR. As duas pendentes são PESQUISA em andamento,
    /// com o que já foi examinado e o que falta: uma busca que não encontrou a leitura não vira "sem API".
    /// </summary>
    [Fact]
    public void Microsoft365_TodasAsReferenciasClassificadas_PendenteSoComPesquisaDeclarada()
    {
        var coverage = KnightReferenceCatalog.Coverage();
        var m365 = coverage.ByPlatform.Single(p => p.Key == nameof(KnightPlatform.Microsoft365));
        m365.Total.Should().Be(89);
        m365.Implemented.Should().Be(81);
        m365.Partial.Should().Be(2, "8.6.1 do Teams e 2.1.11 do Defender (lista de extensões própria do AEGIS)");
        m365.RequiresAccess.Should().Be(1, "8.4.1 do Teams — ver Teams_TemAs17ReferenciasClassificadas...");
        m365.ApiLimitation.Should().Be(3, "Sway, Defender for Cloud Apps (2.4.3) e correção automatizada do AIR (2.4.5): nenhum método publicado");
        m365.PreviewOnly.Should().Be(1, "Forms: a leitura existe só na versão beta do Microsoft Graph");
        m365.ManualOnly.Should().Be(1, "2.2.1 — quais contas são de emergência é decisão organizacional");
        m365.Pending.Should().Be(0, "as duas pesquisas pendentes foram fechadas com os métodos examinados");

        void Service(KnightService s, int total, int implemented, int partial = 0, int api = 0, int manual = 0, int pending = 0, int preview = 0)
        {
            var g = coverage.ByService.Single(x => x.Key == s.ToString());
            (g.Total, g.Implemented, g.Partial, g.ApiLimitation, g.ManualOnly, g.Pending, g.PreviewOnly)
                .Should().Be((total, implemented, partial, api, manual, pending, preview), s.ToString());
        }
        Service(KnightService.DefenderForOffice365, 21, 17, partial: 1, manual: 1, api: 2);
        Service(KnightService.Purview, 5, 5);
        Service(KnightService.SharePointOnline, 13, 13);
        Service(KnightService.Intune, 2, 2);
        Service(KnightService.Fabric, 12, 12);
        Service(KnightService.Forms, 1, 0, preview: 1);
        Service(KnightService.Sway, 1, 0, api: 1);
    }

    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-04] Os controles dos cinco serviços novos só consomem capacidades que o COLETOR REAL da
    /// fonte produz, declaram o serviço e a fonte certos, e cada referência avaliada é citada por um — e só um — controle.
    /// </summary>
    [Fact]
    public void ControlesDosServicosM365_SoConsomemCapacidadesDoColetorReal_ECadaReferenciaTemUmDono()
    {
        var blocos = new (IReadOnlyList<KnightIndicatorDefinition> Defs, KnightSourceType Source, KnightService Service, string Prefix, int Count)[]
        {
            (IntuneControls.Definitions, KnightSourceType.MicrosoftIntune, KnightService.Intune, "AK-INT-", 2),
            (SharePointControls.Definitions, KnightSourceType.MicrosoftSharePoint, KnightService.SharePointOnline, "AK-SPO-", 13),
            (FabricControls.Definitions, KnightSourceType.MicrosoftFabric, KnightService.Fabric, "AK-FAB-", 12),
            (DefenderForOffice365Controls.Definitions, KnightSourceType.MicrosoftDefenderForOffice365, KnightService.DefenderForOffice365, "AK-MDO-", 18),
            (PurviewControls.Definitions, KnightSourceType.MicrosoftPurview, KnightService.Purview, "AK-PUR-", 5),
        };
        var coverage = KnightReferenceCatalog.Coverage();
        foreach (var (defs, source, service, prefix, count) in blocos)
        {
            defs.Should().HaveCount(count, prefix);
            foreach (var d in defs)
            {
                d.Id.Should().StartWith(prefix);
                KnightCatalog.Indicators.Should().Contain(x => x.Id == d.Id, "o controle precisa estar no catálogo ativo");
                KnightCollectorCapabilities.IsActive(d).Should().BeTrue(d.Id);
                d.Service.Should().Be(service, d.Id);
                d.Sources.Should().BeEquivalentTo(new[] { source }, d.Id);
                d.References.Should().ContainSingle(d.Id);
                KnightControlProfiles.RequiredCapabilitiesOf(d.Id)
                    .Should().OnlyContain(c => KnightCollectorCapabilities.Produces(source).Contains(c), d.Id);
            }
            foreach (var c in coverage.Controls.Where(c => c.Control.Service == service
                         && c.Disposition is KnightReferenceDisposition.Implemented or KnightReferenceDisposition.Partial))
                c.IndicatorIds.Should().ContainSingle(id => id.StartsWith(prefix), c.Control.Key);
        }
    }

    [Fact]
    public void ControlesDoTeams_SoConsomemCapacidadesDoColetorReal()
    {
        foreach (var d in TeamsConfigurationControls.Definitions)
        {
            KnightCollectorCapabilities.IsActive(d).Should().BeTrue(d.Id);
            d.Service.Should().Be(KnightService.Teams);
            d.Sources.Should().BeEquivalentTo(new[] { KnightSourceType.MicrosoftTeams },
                $"{d.Id} lê a configuração do Teams — aplicá-lo ao Entra ID o deixaria não avaliado para sempre");
            // Um controle sem vínculo de referência é EXCEÇÃO, e só se justifica quando o controle não conclui:
            // vincular uma referência a uma regra que não roda declararia cobertura inexistente. Hoje há um caso,
            // e ele é nominal — qualquer outro controle precisa citar a referência que avalia.
            if (d.Id == "AK-TEAMS-007")
                d.References.Should().BeEmpty(
                    "AK-TEAMS-007 preserva a configuração como evidência mas não conclui: a leitura que diria se "
                    + "ela governa não é suportada com autenticação de aplicativo. A disposição do 8.4.1 está "
                    + "declarada em KnightReferenceDispositions, fora do fluxo de avaliação.");
            else
                d.References.Should().NotBeEmpty(d.Id);
        }
    }

    /// <summary>
    /// [Revisão dirigida] DEFEITO REPRODUZIDO: o motivo de “não avaliado” de AK-TEAMS-007 passou de 500
    /// caracteres e o PostgreSQL recusou a gravação (22001). O SQLite das baterias locais NÃO valida tamanho, e
    /// por isso o defeito só apareceu no banco real — um texto que não cabe no banco não chega a ninguém.
    ///
    /// Esta verificação vale para TODOS os controles, e não só para o que quebrou: qualquer texto que a avaliação
    /// possa gravar precisa caber na coluna que o guarda. O limite vem do mapeamento (KnightIndicator), não de um
    /// número escolhido aqui.
    /// </summary>
    [Fact]
    public void TextosQueAAvaliacaoGrava_CabemNaColunaQueOsGuarda()
    {
        const int limiteDoIndicador = 500;
        const string prefixo = "Não avaliado: ";   // o que a apresentação acrescenta ao motivo

        var contexto = new KnightEvaluationContext(KnightFactSet.Empty, Array.Empty<KnightCapabilityStatus>(),
            null, KnightTenantConfiguration.Empty, Array.Empty<KnightAffectedObjectEvidence>(), DateTimeOffset.UtcNow);

        foreach (var d in KnightCatalog.Indicators.Where(d => d.Evaluate is not null))
        {
            // Sem coleta alguma, todo controle cai no seu motivo de não avaliado — é o texto mais longo que ele
            // publica, e o que o banco recusou.
            var motivo = d.Evaluate!(contexto).NotEvaluatedReason;
            if (motivo is null) continue;

            (prefixo.Length + motivo.Length).Should().BeLessThanOrEqualTo(limiteDoIndicador,
                $"{d.Id} grava este motivo, e o PostgreSQL recusa o que não couber: \"{motivo}\"");
        }
    }

    [Fact]
    public void Disposicoes_DeclaradasSoParaChavesExistentes_ENuncaParaAlgoImplementado()
    {
        var linked = KnightCatalog.Indicators.SelectMany(d => d.References).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, declared) in KnightReferenceDispositions.All)
        {
            KnightReferenceCatalog.Find(key).Should().NotBeNull(key);
            linked.Should().NotContain(key, $"{key} não pode ser declarado {declared.Disposition} e citado por um controle ao mesmo tempo");
            declared.Note.Should().NotBeNullOrWhiteSpace();
            declared.Disposition.Should().BeOneOf(KnightReferenceDisposition.ApiLimitation, KnightReferenceDisposition.PreviewOnly,
                KnightReferenceDisposition.ManualOnly, KnightReferenceDisposition.RequiresAccess);
        }
    }

    [Fact]
    public void LimitacaoDeApi_DizQualVersaoOuCanalFalta_ESemApiPrivada()
    {
        foreach (var (key, d) in KnightReferenceDispositions.All.Where(x => x.Value.Disposition == KnightReferenceDisposition.ApiLimitation))
        {
            d.Note.Should().MatchRegex("beta|versão estável", key);
            d.Note.Should().Contain("Verificação manual", key);
            d.Note.Should().NotContainAny("main.iam.ad.ext.azure.com", "api.interfaces.records.teams");
        }
    }

    /// <summary>
    /// [AEGIS-KNIGHT-COVERAGE-04] "Sem versão estável" não é "sem API": quando a leitura existe em versão beta/preview, a
    /// referência é "só em preview" e a nota diz isso; "sem método" só quando nem a beta/preview tem a operação.
    /// </summary>
    [Fact]
    public void SoEmPreview_EDiferenteDeSemMetodo_ENotaDizQualDosDois()
    {
        foreach (var (key, d) in KnightReferenceDispositions.All)
        {
            if (d.Disposition == KnightReferenceDisposition.PreviewOnly)
            {
                d.Note.Should().StartWith("Leitura disponível só em versão", key);
                d.Note.Should().MatchRegex("beta|preview", key);
                d.Note.Should().Contain("Verificação manual", key);
            }
            else if (d.Disposition == KnightReferenceDisposition.ApiLimitation)
            {
                d.Note.Should().StartWith("Sem método publicado", key);
                d.Note.Should().NotContain("só em versão", key);
            }
        }
        var t = KnightReferenceCatalog.Coverage().Total;
        t.PreviewOnly.Should().Be(20, "7 do Entra ID, 1 do Microsoft 365 (Forms) e 12 do Azure (diagnóstico e contatos de segurança)");
        t.ApiLimitation.Should().Be(14, "10 do Entra ID, 3 do Microsoft 365 e 1 do Azure (diagnóstico do Intune)");
        t.Pending.Should().Be(0, "as nove pesquisas foram fechadas");
        KnightReferenceDispositions.ResearchKeys.Should().BeEmpty();
    }

    [Fact]
    public void ReferenciaDeclaradaEmRegraSemColeta_NaoContaComoImplementada()
    {
        var semColeta = new KnightIndicatorDefinition("AK-TESTE-001", "1", "t", KnightIndicatorCategory.TenantConfiguration, SeverityLevel.Low,
            new HashSet<KnightSourceType> { KnightSourceType.MicrosoftEntraId }, Array.Empty<string>(), Array.Empty<string>(), "r", "e",
            _ => new KnightIndicatorOutcome(KnightIndicatorStatus.NotEvaluated, "x", 0, "x"))
        {
            References = new[] { new KnightReferenceLink("CIS-M365-7.0.0:5.1.2.5") },
        };
        var status = KnightReferenceCatalog.Coverage(new[] { semColeta }).Controls.Single(c => c.Control.Key == "CIS-M365-7.0.0:5.1.2.5");
        status.Disposition.Should().Be(KnightReferenceDisposition.Pending);
        status.Note.Should().Contain("fora do fluxo ativo");
    }

    [Fact]
    public void ControleCitandoReferenciaInexistente_EhDefeitoDeCatalogo()
    {
        var def = KnightCatalog.Indicators.First(d => d.Id == "AK-ENTRA-016") with
        {
            References = new[] { new KnightReferenceLink("CIS-M365-7.0.0:99.99") },
        };
        FluentActions.Invoking(() => KnightReferenceCatalog.Coverage(new[] { def })).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void TodoControleAtivo_TemPerfilCompleto_ComRiscoEImpactoProprios()
    {
        var active = KnightCatalog.Indicators.Where(d => d.Sources.Any(s => s != KnightSourceType.Demo)).ToList();
        foreach (var d in active)
        {
            var p = KnightControlProfiles.For(d.Id);
            p.Should().NotBeNull(d.Id);
            p!.Impact.Should().NotBeNullOrWhiteSpace(d.Id);
            p.Rationale.Should().NotBeNullOrWhiteSpace(d.Id);
            p.RequiredCapabilities.Should().NotBeEmpty(d.Id);
            p.Documentation.All(r => r.Url == null || r.Url.StartsWith("https://learn.microsoft.com/")).Should().BeTrue(d.Id);
        }
        active.Select(d => KnightControlProfiles.For(d.Id)!.Impact).Should().OnlyHaveUniqueItems("texto genérico repetido não explica o controle");
        active.Select(d => KnightControlProfiles.For(d.Id)!.Rationale).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// O impacto responde a outra pergunta que o risco: não é o risco reescrito, e não afirma alcance que a condição
    /// observada não sustenta (comprometimento consumado, acesso a tudo, controle do ambiente inteiro).
    /// </summary>
    [Fact]
    public void Impacto_NaoRepeteORisco_ENaoAfirmaAlcanceQueACondicaoNaoSustenta()
    {
        var semSustentacao = new[] { "todos os dados", "controle total", "acesso total", "todo o ambiente", "qualquer sistema", "foi comprometid" };
        foreach (var d in KnightCatalog.Indicators.Where(x => x.Sources.Any(s => s != KnightSourceType.Demo)))
        {
            var p = KnightControlProfiles.For(d.Id)!;
            foreach (var termo in semSustentacao)
                p.Impact!.Should().NotContain(termo, $"{d.Id}: o impacto não pode afirmar o que a condição observada não sustenta");
            Overlap(p.Impact!, p.Rationale).Should().BeLessThan(0.5,
                $"{d.Id}: o impacto é a consequência possível, não o risco repetido com outras palavras");
        }
    }

    /// <summary>Fração das palavras de conteúdo em comum entre dois textos (Jaccard), em minúsculas.</summary>
    private static double Overlap(string a, string b)
    {
        static HashSet<string> Words(string s) => new(
            s.ToLowerInvariant().Split(new[] { ' ', ',', '.', ';', ':', '(', ')', '—', '–', '“', '”', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 3), StringComparer.Ordinal);
        var x = Words(a);
        var y = Words(b);
        var union = x.Count + y.Count - x.Count(y.Contains);
        return union == 0 ? 0 : (double)x.Count(y.Contains) / union;
    }

    [Fact]
    public void ControlesDeConfiguracao_SoConsomemCapacidadesDoColetorReal()
    {
        foreach (var d in EntraConfigurationControls.Definitions)
        {
            KnightCollectorCapabilities.IsActive(d).Should().BeTrue(d.Id);
            d.Service.Should().Be(KnightService.EntraId);
            d.References.Should().NotBeEmpty(d.Id);
        }
    }

    [Fact]
    public void ResumoCongelado_RelidoIgualAoPublicado()
    {
        var json = KnightReferenceCoverageSnapshot.Serialize(KnightReferenceCatalog.Coverage());
        var back = KnightReferenceCoverageSnapshot.Deserialize(json)!;
        back.Total.Implemented.Should().Be(KnightReferenceCatalog.Coverage().Total.Implemented);
        KnightReferenceCoverageSnapshot.Serialize(KnightReferenceCatalog.Coverage()).Should().Be(json, "mesma entrada, mesmos bytes");
        KnightReferenceCoverageSnapshot.Deserialize("{ilegível").Should().BeNull();
    }

    [Fact]
    public void HashDaFotografia_ProtegeImpactoECobertura_SemMudarAsAnteriores()
    {
        var s = new PostureSnapshot
        {
            TenantId = Guid.NewGuid(), Type = PostureSnapshotType.Knight, SchemaVersion = PostureSnapshotSchema.KnightReportVersion,
            FormulaVersion = "knight-score-v1", CatalogVersion = "ak-knight-v3", SemanticFamily = "knight:MicrosoftEntraId",
            SourceType = KnightSourceType.MicrosoftEntraId, CapturedAt = DateTimeOffset.UnixEpoch, Coverage = 100,
        };
        s.Indicators.Add(new PostureSnapshotIndicator { IndicatorId = "AK-ENTRA-001", Title = "t", Evidence = "e", SourceType = KnightSourceType.MicrosoftEntraId });
        var legacy = PostureSnapshotHasher.Compute(s);

        // Fotografia anterior (sem os campos novos): o hash não muda por existir a extensão.
        PostureSnapshotHasher.Compute(s).Should().Be(legacy);

        s.Indicators.Single().Impact = "impacto";
        s.ReferenceCoverageJson = "{}";
        s.ContentHash = PostureSnapshotHasher.Compute(s);
        s.ContentHash.Should().NotBe(legacy);
        PostureSnapshotHasher.Verify(s).Should().BeTrue();

        s.Indicators.Single().Impact = "outro impacto";
        PostureSnapshotHasher.Verify(s).Should().BeFalse("o impacto publicado é conteúdo assinado");
        s.Indicators.Single().Impact = "impacto";
        s.ReferenceCoverageJson = "{\"x\":1}";
        PostureSnapshotHasher.Verify(s).Should().BeFalse("a cobertura publicada é conteúdo assinado");
    }

    [Fact]
    public void ComposicaoNomeada_NaoTransformaAplicacaoEmUsuario()
    {
        KnightObjectNouns.Composition(new[] { KnightAffectedObjectKind.User, KnightAffectedObjectKind.User, KnightAffectedObjectKind.ServicePrincipal, KnightAffectedObjectKind.Group })
            .Should().Be("4 identidades: 2 contas de usuário, 1 aplicação e 1 grupo");
        KnightObjectNouns.Composition(new[] { KnightAffectedObjectKind.Domain }).Should().Be("1 domínio");
        KnightObjectNouns.Composition(new[] { KnightAffectedObjectKind.User, KnightAffectedObjectKind.User }, 75)
            .Should().Be("75 contas de usuário (lista preservada: 2 contas de usuário)");
        KnightObjectNouns.Composition(new[] { KnightAffectedObjectKind.DirectoryRole, KnightAffectedObjectKind.Policy })
            .Should().Be("2 itens: 1 política e 1 papel de diretório");
    }
}
