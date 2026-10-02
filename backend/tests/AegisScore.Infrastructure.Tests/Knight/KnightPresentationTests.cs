using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AegisScore.Application.Knight;
using AegisScore.Application.Posture;
using AegisScore.Application.Posture.Export;
using AegisScore.Domain;
using AegisScore.Infrastructure.Posture.Export;
using FluentAssertions;
using UglyToad.PdfPig;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Knight;

/// <summary>
/// [AEGIS-KNIGHT-PRESENTATION-01] Apresentação dos controles e glossário: os itens recorrentes citam os controles pelo título
/// CONGELADO (código como informação secundária), o glossário é um só para tela, HTML e PDF, e o CSV mantém código e título
/// em campos separados. Nada aqui muda critério, nota, cobertura ou coleta.
/// </summary>
public sealed class KnightPresentationTests
{
    // Título CONGELADO diferente do título do catálogo atual: prova que o relatório não troca o histórico pelo presente.
    private const string TituloCongelado = "Título histórico congelado do controle 001";

    [Fact]
    public void Glossario_PalavraInteira_SensivelAMaiusculas_ComPlural_ESemInterpretarCodigos()
    {
        var used = KnightGlossary.UsedIn(new[] { "Exige MFA e expõe APIs", "Código AK-AZ-IAM-004", "rbac em minúsculas", null }).Select(t => t.Term);
        used.Should().Equal("API", "MFA");
        KnightGlossary.UsedIn(new[] { "AK-AZ-IAM-004" }).Should().BeEmpty("um código interno não é interpretado por prefixo");
        KnightGlossary.UsedIn(Array.Empty<string>()).Should().BeEmpty();
    }

    [Fact]
    public void Glossario_Unico_ComOsTermosPedidos_ESemDefinicaoVazia()
    {
        KnightGlossary.Terms.Select(t => t.Term).Should().OnlyHaveUniqueItems().And.Contain(new[] { "IAM", "RBAC", "MFA", "CSPM", "DLP" });
        KnightGlossary.Terms.Should().OnlyContain(t => t.Meaning.Length > 0 && t.Explanation.Length > 0);
        KnightGlossary.Terms.Select(t => t.Term).Should().BeInAscendingOrder(StringComparer.Ordinal);
        KnightGlossary.IdentifierExplanation.Should().Contain("AK-AZ-IAM-004").And.Contain("título");
    }

    [Fact]
    public void TipoDoRecurso_LidoDoIdentificadorCongelado_SemTipoConhecidoFicaGenerico()
    {
        const string sub = "/subscriptions/00000000-aaaa-4000-8000-00000000000a";
        KnightObjectNouns.Label(KnightAffectedObjectKind.CloudResource, sub).Should().Be("Assinatura");
        KnightObjectNouns.Label(KnightAffectedObjectKind.CloudResource, sub + "/resourceGroups/rg-demo").Should().Be("Grupo de recursos");
        KnightObjectNouns.Label(KnightAffectedObjectKind.CloudResource, sub + "/resourceGroups/rg-demo/providers/Microsoft.Storage/storageAccounts/stdemo")
            .Should().Be("Conta de armazenamento");
        KnightObjectNouns.Label(KnightAffectedObjectKind.CloudResource, sub + "/resourceGroups/rg-demo/providers/Microsoft.Web/sites/app-demo/slots/staging")
            .Should().Be("Slot do App Service");
        KnightObjectNouns.Label(KnightAffectedObjectKind.CloudResource, sub + "/resourceGroups/rg/providers/Contoso.Desconhecido/coisas/x")
            .Should().Be("Recurso de nuvem", "tipo sem rótulo conhecido não é inventado");
        KnightObjectNouns.Label(KnightAffectedObjectKind.CloudResource, "região-brazilsouth").Should().Be("Recurso de nuvem");
        KnightObjectNouns.Label(KnightAffectedObjectKind.User, sub).Should().Be("Conta de usuário", "só recurso de nuvem é refinado");
    }

    [Fact]
    public void Modelo_ItensRecorrentes_CitamControlesPeloTituloCongelado_EGlossarioDaFotografia()
    {
        var model = KnightReportModelBuilder.Build(Snapshot(), integrityVerified: true);

        var top = model.TopObjects.First();
        top.ExternalId.Should().Be("u-1");
        top.ControlCount.Should().Be(5);
        top.ControlIds.Should().HaveCount(5, "o campo anterior continua no modelo");
        top.Controls!.Select(c => c.Id).Should().Equal(top.ControlIds);
        top.Controls!.Single(c => c.Id == "AK-ENTRA-001").Title.Should().Be(TituloCongelado);

        KnightReportModelBuilder.RefOf(model.Controls.ToDictionary(c => c.Id, c => c.Title), "AK-NAO-EXISTE").Title
            .Should().BeNull("sem o controle na fotografia, o título fica indisponível — nunca vem do catálogo atual");

        // MFA e RBAC vêm dos textos congelados; IAM vem do rótulo do domínio ("IAM/RBAC"), nunca do código AK-….
        model.Glossary!.Select(t => t.Term).Should().Contain(new[] { "MFA", "RBAC", "IAM" }).And.NotContain("CSPM");
        model.Controls.Single(c => c.Id == "AK-ENTRA-001").GlossaryTerms.Should().Contain("MFA");
        model.Controls.Single(c => c.Id == "AK-ENTRA-002").GlossaryTerms.Should().NotContain("MFA", "cada controle lista só as siglas dos próprios textos");
        model.IdentifierExplanation.Should().Be(KnightGlossary.IdentifierExplanation);
    }

    [Fact]
    public void Html_NovaTabelaDeAfetados_GlossarioEReferenciasClicaveis_Offline()
    {
        var html = Encoding.UTF8.GetString(PostureSnapshotHtmlWriter.Write(Snapshot(), integrityVerified: true));

        html.Should().Contain("Conta ou recurso afetado").And.Contain("Quantidade de controles").And.Contain("Controles relacionados");
        html.Should().NotContain("text:'Quais'", "a coluna de códigos compactados foi substituída");
        html.Should().Contain("Mostrar todos os ").And.Contain("aria-expanded").And.Contain("tab-glossary").And.Contain("Termos técnicos deste controle");
        html.Should().Contain("\"glossaryTerms\":[").And.Contain("\"identifierExplanation\"");
        html.Should().Contain("congelado do controle 001", "o título congelado vai na ilha de dados (acentos escapados pelo encoder)");
        html.Should().Contain("default-src 'none'").And.NotContain("<script src").And.NotContain("<link");
    }

    [Fact]
    public void Pdf_TitulosJuntoDosCodigos_EGlossarioUmaVez()
    {
        var snapshot = Snapshot();
        var texto = Pdf(PostureSnapshotPdfWriter.Write(snapshot));

        texto.Should().Contain("Glossário");
        texto.Should().Contain($"achado: {TituloCongelado} (AK-ENTRA-001)");
        CountOf(Deaccent(texto), "Autenticacao multifator").Should().Be(1, "cada sigla é definida uma única vez, no glossário do fim");

        var limitacao = new ReportLimitation("IdentityRiskyUsers", "Usuários de risco", "InsufficientPermission", "Autorização recusada",
            null, null, new[] { "AK-ENTRA-001", "AK-NAO-EXISTE" }, "Conceder a leitura.", null);
        var titulos = snapshot.Indicators.ToDictionary(i => i.IndicatorId, i => i.Title);
        PostureSnapshotPdfWriter.LimitationLine(limitacao, titulos).Should()
            .Contain($"{TituloCongelado} (AK-ENTRA-001)").And.Contain("AK-NAO-EXISTE (título não disponível nesta fotografia)");
        PostureSnapshotPdfWriter.LimitationLine(limitacao).Should().Contain("AK-ENTRA-001, AK-NAO-EXISTE", "sem títulos, o formato anterior");
    }

    [Fact]
    public void Csv_CodigoETituloEmCamposSeparados()
    {
        var csv = Encoding.UTF8.GetString(PostureSnapshotCsvWriter.Write(Snapshot())).TrimStart('﻿');
        var header = csv.Split("\r\n")[0].Split(';');
        var id = Array.IndexOf(header, "IndicatorId");
        id.Should().BeGreaterThanOrEqualTo(0);
        header[id + 1].Should().Be("Title");
        csv.Should().Contain("AK-ENTRA-001;" + TituloCongelado + ";");
    }

    // ---- infraestrutura ----------------------------------------------------------------------------------

    private static int CountOf(string text, string part)
    {
        var n = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private static string Deaccent(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString();
    }

    private static string Pdf(byte[] bytes)
    {
        using var pdf = PdfDocument.Open(bytes);
        return string.Join(" ", pdf.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text))));
    }

    /// <summary>Fotografia v2 sintética: uma conta afetada em cinco controles (lista longa), um plano de ação e siglas nos textos.</summary>
    private static PostureSnapshot Snapshot()
    {
        var s = new PostureSnapshot
        {
            TenantId = Guid.NewGuid(), Type = PostureSnapshotType.Knight, SchemaVersion = PostureSnapshotSchema.KnightReportVersion,
            FormulaVersion = "knight-score-v1", CatalogVersion = "ak-knight-v9", SemanticFamily = "knight:MicrosoftEntraId",
            SourceType = KnightSourceType.MicrosoftEntraId, SourceLabel = "Microsoft Entra ID", ClientName = "Cliente Demo",
            CapturedAt = DateTimeOffset.Parse("2026-10-02T10:00:00Z", CultureInfo.InvariantCulture),
            Score = 0, Coverage = 100, NonCompliantCount = 5, ContentHash = "h",
        };
        for (var n = 1; n <= 5; n++)
        {
            var id = $"AK-ENTRA-00{n}";
            s.Indicators.Add(new PostureSnapshotIndicator
            {
                IndicatorId = id, Title = n == 1 ? TituloCongelado : $"Controle sintético {n} com papel RBAC",
                Description = n == 1 ? "Contas sem MFA registrado." : null,
                Category = KnightIndicatorCategory.PrivilegedAccess, Severity = SeverityLevel.High, Status = KnightIndicatorStatus.Exposed,
                Evidence = "1 conta.", AffectedObjectCount = 1, SourceType = KnightSourceType.MicrosoftEntraId,
                CollectedAt = DateTimeOffset.Parse("2026-10-02T09:00:00Z", CultureInfo.InvariantCulture),
                HasAffectedDetail = true, AffectedDetailComplete = true,
            });
            s.Objects.Add(new PostureSnapshotObject
            {
                IndicatorId = id, Relation = KnightObjectRelation.Affected, Kind = KnightAffectedObjectKind.User,
                ExternalId = "u-1", DisplayName = "Ana Demo", UserPrincipalName = "ana@demo.example.com",
            });
        }
        s.ActionItems.Add(new PostureSnapshotActionItem
        {
            ActionPlanId = Guid.NewGuid(), IndicatorId = "AK-ENTRA-001", Title = "Registrar segundo fator", NextStep = "Revisar contas.",
        });
        return s;
    }
}
