using System;
using System.Linq;
using System.Security.Cryptography;
using AegisScore.Application.Nist;
using AegisScore.Application.Posture;
using AegisScore.Application.Posture.Export;
using AegisScore.Domain;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;

namespace AegisScore.Infrastructure.Tests.Nist;

/// <summary>
/// [AEGIS-NIST-AI-ASSIST-01] Fotografias de maturidade publicadas ANTES da assistência de IA continuam idênticas: o JSON
/// congelado (sem interpretação nem procedência assistida) lê-se sem perda, a impressão digital não muda e HTML e CSV saem
/// byte a byte iguais aos da baseline <c>df84baf</c> — os hashes abaixo foram capturados com o código da baseline, antes de
/// qualquer alteração deste pacote. Relatório antigo nunca vira versão "enriquecida" com o contexto de hoje.
/// </summary>
public sealed class NistAssistReportCompatibilityTests
{
    // Capturados com o código da baseline df84baf (pré-assistência) para este relatório sintético.
    private const string BaselineFingerprint = "140b6c1edc40b0b6a4465907e729d77620ed863e12f2571b41a7f122fb9b9f2a";
    private const string BaselineHtmlSha256 = "93205557730c78c325c76552a2e1289b9449a425f1710d9181edb8a88ab2fcee";
    private const string BaselineCsvSha256 = "5b304851a49179a9a2d7cd11aaef82b89ec2d4ce0b634a0550de1a92bf54a760";

    [Fact]
    public void FotografiaAntiga_SemAssistencia_LeIgual_MesmaImpressaoDigital_EExportacoesIdenticas()
    {
        var json = NistReportCanonical.Serialize(LegacyReport());
        var read = NistReportCanonical.Deserialize(json);

        NistReportCanonical.Serialize(read).Should().Be(json, "o relatório antigo é relido e reescrito sem campos novos");
        NistReportCanonical.Fingerprint(read).Should().Be(BaselineFingerprint);

        var snapshot = Snapshot(json);
        using var scope = new AssertionScope();
        Sha(NistReportHtmlWriter.Write(snapshot, read, integrityVerified: true)).Should().Be(BaselineHtmlSha256);
        Sha(NistReportCsvWriter.Write(snapshot, read)).Should().Be(BaselineCsvSha256);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static PostureSnapshot Snapshot(string json)
    {
        var s = new PostureSnapshot
        {
            Id = Guid.Parse("5a5a5a5a-0303-0303-0303-000000000001"),
            TenantId = Guid.Parse("aaaaaaaa-0303-0303-0303-0000000000a1"),
            Type = PostureSnapshotType.NistMaturity,
            SchemaVersion = "posture-snapshot-nist-maturity-v1",
            FormulaVersion = "aegis-methodology-v1",
            CatalogVersion = "NIST CSF 2.0",
            SemanticFamily = "nist-maturity:aegis-methodology-v1:NIST CSF 2.0:legado",
            SourceLabel = "Avaliação sintética · Matriz",
            CapturedAt = new DateTimeOffset(2026, 9, 30, 18, 0, 0, TimeSpan.Zero),
            Coverage = 66.7,
            EvaluatedItems = 1,
            EligibleItems = 3,
            NotApplicableCount = 1,
            NotEvaluatedCount = 1,
            ClientName = "Cliente Sintético",
            NistAssessmentId = Guid.Parse("a0000000-0303-0303-0303-000000000001"),
            NistCycleId = Guid.Parse("c0000000-0303-0303-0303-000000000001"),
            NistScopeId = Guid.Parse("50000000-0303-0303-0303-000000000001"),
            NistCycleName = "Setembro 2026",
            MaturityCurrent = 2,
            MaturityTarget = 4,
            MaturityGap = 2,
            NistReportJson = json,
            CreatedAt = new DateTimeOffset(2026, 9, 30, 18, 0, 0, TimeSpan.Zero),
        };
        s.ContentHash = PostureSnapshotHasher.Compute(s);
        return s;
    }

    /// <summary>Relatório no formato da baseline: nenhuma interpretação executiva nem procedência assistida.</summary>
    internal static NistMaturityReport LegacyReport()
    {
        var at = new DateTimeOffset(2026, 9, 28, 14, 30, 0, TimeSpan.Zero);
        var evidenceId = Guid.Parse("e0000000-0303-0303-0303-000000000001");
        var findingId = Guid.Parse("f0000000-0303-0303-0303-000000000001");
        var procedureId = Guid.Parse("b0000000-0303-0303-0303-000000000001");
        var planId = Guid.Parse("d0000000-0303-0303-0303-000000000001");

        var plan = new NistReportPlan(planId, "Formalizar a revisão da política", "Aprovar o rito anual de revisão.",
            new NistReportPerson("Analista Demo", "User", null), "Segurança", new DateOnly(2026, 11, 30), "EmAndamento", "Em andamento",
            false, "Registrar a execução.", null, null, null, null, null, null, null, null, false, at, at);
        var finding = new NistReportFinding(findingId, "GV.PO-01", "Política de segurança estabelecida", "Política sem revisão registrada",
            "Não há registro da última revisão da política.", "A política pode ficar desatualizada frente aos riscos atuais.",
            "Decisões baseadas em diretrizes antigas.", "Medium", "Média", "Sem evidência de revisão nos últimos 24 meses.",
            "High", "Alta", "Base de outras práticas de governança.", "Formalizar a revisão anual.", "Open", "Aberto", null,
            new[] { evidenceId }, "Gestora Demo", at, 2, 4, 2, plan, "Em andamento");

        var evaluated = new NistReportSubcategory("GV.PO-01", "Política de segurança estabelecida", "GV", "GV.PO",
            "Policy for managing cybersecurity risks is established.", NistSubcategoryStates.Evaluated, "Avaliada",
            2, 4, 2, false, "Política existe, sem revisão registrada.", "Entrevista confirmou a ausência de rito.", null,
            "Sem registro de revisão.", "Diretrizes desatualizadas.", "Instituir revisão anual.",
            new NistReportPerson("Diretoria de Riscos", "Text", null), "Analista Demo", "Revisor Demo", "Gestora Demo", at,
            "Analyst", null, NistReviewStates.Approved, "Revisão aprovada", "Revisor Demo", at, null, 3,
            new[]
            {
                new NistReportProcedure(procedureId, "Interview", "Entrevistar", "Entrevistar a diretoria sobre o rito de revisão.",
                    "Performed", "Realizado", "PartiallySatisfactory", "Parcialmente satisfatório", "Rito informal, sem ata.",
                    new DateOnly(2026, 9, 20), "Gestora Demo", at, new[] { evidenceId }, "Analyst"),
            },
            new[]
            {
                new NistReportEvidence(evidenceId, "Manual", "Registro do analista", "Interview", "Entrevista com a diretoria", "Confirma ausência de ata.",
                    null, null, "Registro do analista", null, at, at, "Gestora Demo"),
            },
            new[] { findingId });
        var pending = new NistReportSubcategory("PR.AA-01", "Identidades e credenciais gerenciadas", "PR", "PR.AA",
            "Identities and credentials are managed.", NistSubcategoryStates.PendingConfirmation, "Aguardando confirmação humana",
            3, 4, null, false, "Herdado da rodada anterior.", null, null, null, null, null,
            new NistReportPerson(null, "None", null), null, null, null, null, "CarriedForward", "Rascunho a partir da rodada \"Agosto\".",
            NistReviewStates.None, "Sem decisão do revisor", null, null, null, 1,
            Array.Empty<NistReportProcedure>(), Array.Empty<NistReportEvidence>(), Array.Empty<Guid>());
        var notApplicable = new NistReportSubcategory("PR.AA-02", "Identidades verificadas", "PR", "PR.AA",
            "Identities are proofed.", NistSubcategoryStates.NotApplicable, "Não se aplica",
            null, null, null, true, "Sem emissão de identidades no escopo.", null, null, null, null, null,
            new NistReportPerson(null, "None", null), null, null, "Gestora Demo", at, "Analyst", null,
            NistReviewStates.None, "Sem decisão do revisor", null, null, null, 1,
            Array.Empty<NistReportProcedure>(), Array.Empty<NistReportEvidence>(), Array.Empty<Guid>());

        var summary = new NistReportSummary(2, 4, 2, 3, 1, 1, 0, 1, 0, 66.7, 1, 1, 1, 1, 1, 0, 0, 1, 0, 1, 0, 1, 0, 0, 1, 0, 0, at);
        return new NistMaturityReport(
            NistMaturityReport.SchemaV1,
            new NistReportClient("Cliente Sintético"),
            new NistReportAssessment(Guid.Parse("a0000000-0303-0303-0303-000000000001"), "Avaliação sintética", null, "InProgress", new DateOnly(2026, 9, 1), null),
            new NistReportCycle(Guid.Parse("c0000000-0303-0303-0303-000000000001"), "Setembro 2026", "Monthly", "Mensal",
                new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), "Open", "Agosto 2026", "Draft", NistLabels.SeedMode("Draft")),
            new NistReportScope(Guid.Parse("50000000-0303-0303-0303-000000000001"), "Matriz", "Escopo sintético"),
            new NistReportCatalog("NIST CSF 2.0", 2, 2, 3),
            new NistReportMethodology("aegis-methodology-v1",
                Enumerable.Range(1, 5).Select(l => new NistReportLevel(l, $"Nível {l}", $"Descrição do nível {l}")).ToList(),
                NistMethodologyText.Statement, NistMethodologyText.AggregationRule, NistMethodologyText.CoverageRule, NistMethodologyText.InstrumentsNote),
            summary,
            new[]
            {
                new NistReportProfile("GV", NistLabels.FunctionName("GV"), null, 2, 4, 2, 1, 1, 1, 1, 0, 1, 0, 0, 0),
                new NistReportProfile("PR", NistLabels.FunctionName("PR"), null, null, null, null, 2, 0, 0, 0, 1, 0, 0, 1, 0),
            },
            new[]
            {
                new NistReportProfile("GV.PO", "Policy", "GV", 2, 4, 2, 1, 1, 1, 1, 0, 1, 0, 0, 0),
                new NistReportProfile("PR.AA", "Identity Management", "PR", null, null, null, 2, 0, 0, 0, 1, 0, 0, 1, 0),
            },
            new[] { evaluated, pending, notApplicable },
            new[] { finding },
            new[] { new NistReportGap("GV.PO-01", "Política de segurança estabelecida", "GV", 2, 4, 2, "Diretrizes desatualizadas.", 1, "Média", "Em andamento") },
            new[] { "1 subcategoria(s) com conteúdo herdado aguardam confirmação humana." },
            new NistReportPublication(new DateTimeOffset(2026, 9, 30, 18, 0, 0, TimeSpan.Zero), "Gestora Demo", new string('a', 64)));
    }
}
