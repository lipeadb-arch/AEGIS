using System;
using System.Collections.Generic;
using System.Linq;
using AegisScore.Application.Knight;
using AegisScore.Application.Nist;
using AegisScore.Application.Posture;
using AegisScore.Domain;

namespace AegisScore.Infrastructure.Tests.Posture;

/// <summary>
/// [AEGIS-ASSESSMENT-VISUALS-01] Fixtures SINTÉTICAS de verificação (organização fictícia, demo.example.com): um relatório KNIGHT
/// consolidado de quatro fontes e uma avaliação NIST com as seis funções, cada um com doze meses de histórico que sobem, caem,
/// ficam estáveis, têm meses sem publicação e uma troca de catálogo que recomeça a série. Não é o cenário de apresentação.
/// </summary>
internal static class AssessmentVisualsFixtures
{
    public static readonly Guid Tenant = Guid.Parse("a5a5a5a5-0707-0707-0707-0000000000a1");
    public static readonly DateTimeOffset PublishedAt = new(2026, 9, 30, 18, 0, 0, TimeSpan.Zero);
    public const string ClientName = "Organização Fictícia (demo.example.com)";

    private static readonly (KnightSourceType Source, string Label, string Catalog)[] Included =
    {
        (KnightSourceType.MicrosoftEntraId, "Microsoft Entra ID", "ak-knight-v10"),
        (KnightSourceType.MicrosoftTeams, "Microsoft Teams", "ak-knight-v10"),
        (KnightSourceType.MicrosoftExchangeOnline, "Exchange Online", "ak-knight-v10"),
        (KnightSourceType.MicrosoftAzure, "Microsoft Azure", "ak-knight-v10"),
    };

    public static string KnightFamily =>
        "knight:consolidated:" + string.Join("+", Included.Select(i => i.Source.ToString()).OrderBy(x => x, StringComparer.Ordinal));

    // ---- KNIGHT ------------------------------------------------------------------------------------------------------

    /// <summary>Fotografia KNIGHT consolidada (v2). Com <paramref name="withHistory"/>, congela o histórico como a publicação faria.</summary>
    public static PostureSnapshot Knight(bool withHistory, HistoryWindow? window = null)
    {
        var s = new PostureSnapshot
        {
            Id = Guid.Parse("5a5a5a5a-0707-0707-0707-000000000001"),
            TenantId = Tenant, Type = PostureSnapshotType.Knight, SchemaVersion = PostureSnapshotSchema.KnightReportVersion,
            FormulaVersion = KnightScoreFormula.Version, CatalogVersion = "ak-knight-consolidated", SemanticFamily = KnightFamily,
            SourceType = KnightSourceType.Consolidated, SourceLabel = KnightConsolidatedLabel.For(Included.Select(i => i.Label).ToList()),
            ClientName = ClientName, CapturedAt = PublishedAt, DataRecency = PublishedAt.AddHours(-6),
            ProfileCatalogVersion = "ak-knight-v10", CreatedAt = PublishedAt,
        };
        var rows = new (string Id, string Title, KnightSourceType Src, string Service, string Domain, SeverityLevel Sev, KnightIndicatorStatus St, int Affected)[]
        {
            ("AK-ENTRA-001", "Contas privilegiadas sem MFA", KnightSourceType.MicrosoftEntraId, "Microsoft Entra ID", "Identity", SeverityLevel.Critical, KnightIndicatorStatus.Exposed, 3),
            ("AK-ENTRA-002", "Papéis privilegiados permanentes", KnightSourceType.MicrosoftEntraId, "Microsoft Entra ID", "IamRbac", SeverityLevel.High, KnightIndicatorStatus.Exposed, 2),
            ("AK-ENTRA-003", "Contas de emergência designadas", KnightSourceType.MicrosoftEntraId, "Microsoft Entra ID", "Identity", SeverityLevel.High, KnightIndicatorStatus.Passed, 0),
            ("AK-ENTRA-004", "Acesso condicional para administradores", KnightSourceType.MicrosoftEntraId, "Microsoft Entra ID", "Identity", SeverityLevel.High, KnightIndicatorStatus.Passed, 0),
            ("AK-ENTRA-005", "Revisões de acesso de convidados", KnightSourceType.MicrosoftEntraId, "Microsoft Entra ID", "Governance", SeverityLevel.Medium, KnightIndicatorStatus.NotEvaluated, 0),
            ("AK-ENTRA-006", "Consentimento de usuários a aplicações", KnightSourceType.MicrosoftEntraId, "Microsoft Entra ID", "IamRbac", SeverityLevel.Medium, KnightIndicatorStatus.Mitigated, 1),
            ("AK-EXO-001", "Encaminhamento externo automático", KnightSourceType.MicrosoftExchangeOnline, "Exchange Online", "DataProtection", SeverityLevel.Medium, KnightIndicatorStatus.Exposed, 4),
            ("AK-EXO-002", "Auditoria de caixas de correio", KnightSourceType.MicrosoftExchangeOnline, "Exchange Online", "Logging", SeverityLevel.High, KnightIndicatorStatus.Passed, 0),
            ("AK-EXO-003", "Identificação de remetentes externos", KnightSourceType.MicrosoftExchangeOnline, "Exchange Online", "Collaboration", SeverityLevel.Low, KnightIndicatorStatus.Error, 0),
            ("AK-EXO-004", "Conectores de entrada legados", KnightSourceType.MicrosoftExchangeOnline, "Exchange Online", "Collaboration", SeverityLevel.Low, KnightIndicatorStatus.NotApplicable, 0),
            ("AK-TEAMS-001", "Federação externa irrestrita", KnightSourceType.MicrosoftTeams, "Microsoft Teams", "Collaboration", SeverityLevel.Medium, KnightIndicatorStatus.Passed, 0),
            ("AK-TEAMS-002", "Convidados ignoram o lobby", KnightSourceType.MicrosoftTeams, "Microsoft Teams", "Collaboration", SeverityLevel.Low, KnightIndicatorStatus.Exposed, 1),
            ("AK-TEAMS-003", "Aplicativos de terceiros liberados", KnightSourceType.MicrosoftTeams, "Microsoft Teams", "Collaboration", SeverityLevel.Medium, KnightIndicatorStatus.Passed, 0),
            ("AK-AZ-NET-001", "Grupos de segurança de rede com SSH aberto", KnightSourceType.MicrosoftAzure, "Rede do Azure", "Network", SeverityLevel.High, KnightIndicatorStatus.Exposed, 2),
            ("AK-AZ-STO-001", "Contas de armazenamento com acesso público", KnightSourceType.MicrosoftAzure, "Armazenamento do Azure", "Storage", SeverityLevel.Medium, KnightIndicatorStatus.Exposed, 1),
            ("AK-AZ-STO-002", "Criptografia com chave gerenciada", KnightSourceType.MicrosoftAzure, "Armazenamento do Azure", "Storage", SeverityLevel.Low, KnightIndicatorStatus.Passed, 0),
            ("AK-AZ-KV-001", "Cofres sem proteção contra exclusão", KnightSourceType.MicrosoftAzure, "Key Vault", "Secrets", SeverityLevel.High, KnightIndicatorStatus.NotEvaluated, 0),
            ("AK-AZ-LOG-001", "Alertas do log de atividades", KnightSourceType.MicrosoftAzure, "Monitor do Azure", "Logging", SeverityLevel.Medium, KnightIndicatorStatus.Passed, 0),
        };
        foreach (var r in rows)
        {
            s.Indicators.Add(new PostureSnapshotIndicator
            {
                TenantId = Tenant, IndicatorId = r.Id, Title = r.Title, Category = KnightIndicatorCategory.PrivilegedAccess,
                Severity = r.Sev, Status = r.St, Evidence = r.Affected > 0 ? $"{r.Affected} item(ns) afetado(s)." : "Sem exposição observada.",
                AffectedObjectCount = r.Affected, SourceType = r.Src, CollectedAt = PublishedAt.AddHours(-6),
                Domain = r.Domain, Service = r.Service, Provider = "Microsoft",
                Platform = r.Src == KnightSourceType.MicrosoftAzure ? "Microsoft Azure" : r.Src == KnightSourceType.MicrosoftEntraId ? "Microsoft Entra ID" : "Microsoft 365",
                Description = $"{r.Title} (descrição sintética).", Recommendation = "Ajustar a configuração (texto sintético).",
                HasAffectedDetail = true, AffectedDetailComplete = true,
            });
            for (var n = 1; n <= r.Affected; n++)
                s.Objects.Add(new PostureSnapshotObject
                {
                    TenantId = Tenant, IndicatorId = r.Id, Relation = KnightObjectRelation.Affected, Kind = KnightAffectedObjectKind.User,
                    ExternalId = $"u-{n}", DisplayName = $"Pessoa Demo {n}", UserPrincipalName = $"pessoa{n}@demo.example.com",
                });
        }
        var score = KnightScoreFormula.Compute(s.Indicators.Select(i => (i.Severity, i.Status)));
        s.Score = score.Score;
        s.Coverage = score.Coverage;
        s.EvaluatedItems = score.EvaluatedCount;
        s.EligibleItems = score.ApplicableCount;
        s.CompliantCount = score.PassedCount;
        s.NonCompliantCount = score.ExposedCount;
        s.MitigatedCount = score.MitigatedCount;
        s.NotEvaluatedCount = score.NotEvaluatedCount;
        s.ErrorCount = score.ErrorCount;
        s.NotApplicableCount = score.NotApplicableCount;
        s.CompositionJson = KnightConsolidatedCompositionJson.Serialize(Composition("ak-knight-v10"));
        if (withHistory)
            s.HistoryJson = FrozenPostureHistoryBuilder.Serialize(
                FrozenPostureHistoryBuilder.Build(PostureSnapshotSummaries.Of(s), KnightPriors(), window ?? HistoryWindow.Default));
        s.ContentHash = PostureSnapshotHasher.Compute(s);
        return s;
    }

    private static IReadOnlyList<KnightConsolidatedSourceEntry> Composition(string catalog) =>
        KnightSourceCatalog.ConsolidationCandidates.Select(src =>
        {
            var inc = Included.FirstOrDefault(i => i.Source == src);
            return inc.Label is null
                ? new KnightConsolidatedSourceEntry(src, KnightSourceCatalog.Label(src), false, "NotAssessed", null, null, null, null,
                    null, null, null, null, null, null, null, null, Array.Empty<string>())
                : new KnightConsolidatedSourceEntry(src, inc.Label, true, "Included", Guid.NewGuid(), "Completed", catalog,
                    PublishedAt.AddHours(-6), 60, 90, 3, 1, 0, 1, 0, 0, Array.Empty<string>());
        }).ToList();

    /// <summary>
    /// Publicações anteriores da MESMA composição: sobe, cai (abr), estável (mai), sem publicação em jan, duas publicações em
    /// jun (vale a última) e troca do catálogo de uma fonte em jul (série recomeça). Mais uma composição DIFERENTE em ago.
    /// </summary>
    public static IReadOnlyList<PostureSnapshotSummaryDto> KnightPriors()
    {
        var list = new List<PostureSnapshotSummaryDto>();
        void Add(int y, int m, int d, double score, double coverage, int applicable, string catalog, string family = "")
        {
            var key = string.Join("|", Included.Select(i => $"{i.Source}={(i.Source == KnightSourceType.MicrosoftEntraId ? catalog : "ak-knight-v10")}").OrderBy(x => x, StringComparer.Ordinal));
            var at = new DateTimeOffset(y, m, d, 12, 0, 0, TimeSpan.Zero);
            list.Add(new PostureSnapshotSummaryDto(Guid.NewGuid(), "Knight", PostureSnapshotSchema.KnightReportVersion, KnightScoreFormula.Version,
                "ak-knight-consolidated", family == "" ? KnightFamily : family, "Consolidated", "Consolidado — 4 fontes", at,
                "Evaluated", score, coverage, (int)Math.Round(applicable * coverage / 100), applicable, 8, 5, 1, 2, 1, 1, at.AddHours(-3),
                new string('b', 64), ClientName, Guid.NewGuid(),
                CompositionLabels: family == "" ? Included.Select(i => i.Label).ToList() : new List<string> { "Microsoft Entra ID", "Microsoft Teams" },
                CompositionKey: family == "" ? key : "outra"));
        }
        Add(2025, 10, 28, 48, 82.4, 17, "ak-knight-v9");
        Add(2025, 11, 27, 52, 82.4, 17, "ak-knight-v9");
        Add(2025, 12, 20, 55, 88.2, 17, "ak-knight-v9");
        Add(2026, 2, 26, 58, 88.2, 17, "ak-knight-v9");
        Add(2026, 3, 30, 61, 88.2, 17, "ak-knight-v9");
        Add(2026, 4, 29, 57, 88.2, 17, "ak-knight-v9");
        Add(2026, 5, 28, 57, 88.2, 17, "ak-knight-v9");
        Add(2026, 6, 10, 59, 88.2, 17, "ak-knight-v9");
        Add(2026, 6, 29, 60, 88.2, 17, "ak-knight-v9");
        Add(2026, 7, 30, 63, 88.2, 17, "ak-knight-v10");
        Add(2026, 8, 28, 64, 88.2, 17, "ak-knight-v10");
        Add(2026, 8, 15, 71, 100, 9, "ak-knight-v10", "knight:consolidated:MicrosoftEntraId+MicrosoftTeams");
        return list;
    }

    // ---- NIST --------------------------------------------------------------------------------------------------------

    public static readonly Guid NistAssessment = Guid.Parse("a0000000-0707-0707-0707-000000000001");
    public static readonly Guid NistScope = Guid.Parse("50000000-0707-0707-0707-000000000001");
    public static string NistFamily => $"nist-maturity:aegis-methodology-v1:NIST CSF 2.0:{NistAssessment:N}:{NistScope:N}";

    public static PostureSnapshot Nist(bool withHistory, HistoryWindow? window = null)
    {
        var r = NistReport();
        var s = new PostureSnapshot
        {
            Id = Guid.Parse("5a5a5a5a-0707-0707-0707-000000000002"),
            TenantId = Tenant, Type = PostureSnapshotType.NistMaturity, SchemaVersion = "posture-snapshot-nist-maturity-v1",
            FormulaVersion = "aegis-methodology-v1", CatalogVersion = "NIST CSF 2.0", SemanticFamily = NistFamily,
            SourceLabel = "Avaliação anual sintética · Matriz", CapturedAt = PublishedAt, Coverage = r.Summary.Coverage,
            EvaluatedItems = r.Summary.Evaluated, EligibleItems = r.Summary.Subcategories, NotApplicableCount = r.Summary.NotApplicable,
            NotEvaluatedCount = r.Summary.Subcategories - r.Summary.Evaluated - r.Summary.NotApplicable,
            DataRecency = r.Summary.LastReviewedAt, ClientName = ClientName, NistAssessmentId = NistAssessment,
            NistCycleId = r.Cycle.Id, NistScopeId = NistScope, NistCycleName = r.Cycle.Name,
            MaturityCurrent = r.Summary.Current, MaturityTarget = r.Summary.Target, MaturityGap = r.Summary.Gap,
            NistReportJson = NistReportCanonical.Serialize(r), CreatedAt = PublishedAt,
        };
        if (withHistory)
            s.HistoryJson = FrozenPostureHistoryBuilder.Serialize(
                FrozenPostureHistoryBuilder.Build(PostureSnapshotSummaries.Of(s), NistPriors(), window ?? HistoryWindow.Default));
        s.ContentHash = PostureSnapshotHasher.Compute(s);
        return s;
    }

    public static IReadOnlyList<PostureSnapshotSummaryDto> NistPriors()
    {
        var list = new List<PostureSnapshotSummaryDto>();
        void Add(int y, int m, double cur, double tgt, double coverage, int evaluated, int na, string cycle, DateOnly ps, DateOnly pe)
        {
            var at = new DateTimeOffset(y, m, 25, 12, 0, 0, TimeSpan.Zero);
            list.Add(new PostureSnapshotSummaryDto(Guid.NewGuid(), "NistMaturity", "posture-snapshot-nist-maturity-v1", "aegis-methodology-v1",
                "NIST CSF 2.0", NistFamily, null, "Avaliação anual sintética · Matriz", at, "NotEvaluated", null, coverage, evaluated, 24, 0, 0, 0,
                24 - evaluated - na, 0, na, at, new string('c', 64), ClientName, null, cur, tgt, Math.Round(tgt - cur, 2),
                NistAssessment, Guid.NewGuid(), NistScope, cycle, PeriodStart: ps, PeriodEnd: pe));
        }
        Add(2025, 10, 2.1, 3.5, 50, 10, 2, "T3 2025", new(2025, 7, 1), new(2025, 9, 30));
        Add(2026, 1, 2.3, 3.5, 66.7, 14, 2, "T4 2025", new(2025, 10, 1), new(2025, 12, 31));
        Add(2026, 4, 2.4, 3.6, 75, 16, 2, "T1 2026", new(2026, 1, 1), new(2026, 3, 31));
        Add(2026, 7, 2.2, 3.6, 83.3, 18, 2, "T2 2026", new(2026, 4, 1), new(2026, 6, 30));
        Add(2026, 8, 2.4, 3.6, 83.3, 18, 2, "Julho 2026", new(2026, 7, 1), new(2026, 7, 31));
        return list;
    }

    /// <summary>Relatório de maturidade sintético: 24 subcategorias nas seis funções, uma função sem avaliação (RC).</summary>
    public static NistMaturityReport NistReport()
    {
        var at = new DateTimeOffset(2026, 9, 28, 14, 30, 0, TimeSpan.Zero);
        var funcs = new (string Code, (string Cat, int? Cur, int? Tgt, string State)[] Subs)[]
        {
            ("GV", new[] { ("GV.OC", (int?)2, (int?)4, "Evaluated"), ("GV.PO", 3, 4, "Evaluated"), ("GV.RR", 2, 3, "Evaluated"), ("GV.SC", null, null, "NotApplicable") }),
            ("ID", new[] { ("ID.AM", (int?)3, (int?)4, "Evaluated"), ("ID.AM", 2, 4, "Evaluated"), ("ID.RA", 2, 3, "Evaluated"), ("ID.RA", null, null, "PendingConfirmation") }),
            ("PR", new[] { ("PR.AA", (int?)3, (int?)4, "Evaluated"), ("PR.AA", 4, 4, "Evaluated"), ("PR.DS", 2, 4, "Evaluated"), ("PR.PS", null, null, "InProgress") }),
            ("DE", new[] { ("DE.CM", (int?)2, (int?)3, "Evaluated"), ("DE.CM", 1, 3, "Evaluated"), ("DE.AE", 2, 3, "Evaluated"), ("DE.AE", null, null, "NotEvaluated") }),
            ("RS", new[] { ("RS.MA", (int?)2, (int?)3, "Evaluated"), ("RS.CO", 2, 3, "Evaluated"), ("RS.AN", null, null, "NotApplicable"), ("RS.MI", null, null, "NotEvaluated") }),
            ("RC", new[] { ("RC.RP", (int?)null, (int?)null, "NotEvaluated"), ("RC.RP", null, null, "NotEvaluated"), ("RC.CO", null, null, "NotEvaluated"), ("RC.CO", null, null, "NotEvaluated") }),
        };
        var subs = new List<NistReportSubcategory>();
        var findings = new List<NistReportFinding>();
        var n = 0;
        foreach (var (code, list) in funcs)
            foreach (var (cat, cur, tgt, state) in list)
            {
                n++;
                var sc = $"{cat}-{n:00}";
                var stateLabel = NistLabels.State(state);
                subs.Add(new NistReportSubcategory(sc, $"Resultado sintético {sc}", code, cat, $"Synthetic outcome {sc}.", state, stateLabel,
                    state == "Evaluated" ? cur : state == "PendingConfirmation" ? 3 : null, state == "Evaluated" ? tgt : null,
                    state == "Evaluated" ? tgt - cur : null, state == "NotApplicable", "Justificativa sintética.", null, null, null,
                    state == "Evaluated" && tgt - cur >= 2 ? "Impacto sintético na operação." : null, null,
                    new NistReportPerson("Área Demo", "Text", null), "Analista Demo", "Revisor Demo", state == "Evaluated" ? "Gestora Demo" : null,
                    state == "Evaluated" ? at : null, state == "PendingConfirmation" ? "CarriedForward" : "Analyst", null,
                    state == "Evaluated" ? NistReviewStates.Approved : NistReviewStates.None, state == "Evaluated" ? "Revisão aprovada" : "Sem decisão do revisor",
                    null, null, null, 1, Array.Empty<NistReportProcedure>(), Array.Empty<NistReportEvidence>(), Array.Empty<Guid>()));
            }
        string[] sev = { "Critical", "High", "High", "Medium", "Low" };
        string[] status = { "Open", "Open", "RiskAccepted", "Open", "Closed" };
        for (var i = 0; i < 5; i++)
        {
            var sub = subs.Where(x => x.State == "Evaluated").ElementAt(i * 2);
            var plan = i == 1 ? null : new NistReportPlan(Guid.NewGuid(), $"Plano sintético {i + 1}", "Ação sintética.",
                new NistReportPerson("Analista Demo", "User", null), "Segurança", new DateOnly(2026, i == 0 ? 8 : 12, 15),
                i == 3 ? "AguardandoValidacao" : "EmAndamento", i == 3 ? "Aguardando validação" : "Em andamento", i == 0, "Registrar a execução.",
                null, null, null, null, null, null, null, null, false, at, at);
            findings.Add(new NistReportFinding(Guid.NewGuid(), sub.Code, sub.Title, $"Achado sintético {i + 1}", "Condição observada sintética.",
                "Risco possível sintético.", "Impacto possível sintético.", sev[i], NistLabels.Severity(sev[i]), "Justificativa da severidade.",
                "High", "Alta", "Justificativa da prioridade.", "Ação recomendada sintética.", status[i], NistLabels.FindingStatus(status[i]), null,
                Array.Empty<Guid>(), "Gestora Demo", at, sub.CurrentLevel, sub.TargetLevel, sub.Gap, plan, plan is null ? "Sem plano" : plan.StatusLabel));
        }

        NistReportProfile Profile(string code, string name, string? fn, IEnumerable<NistReportSubcategory> xs)
        {
            var l = xs.ToList();
            var ev = l.Where(x => x.State == "Evaluated").ToList();
            double? Avg(IEnumerable<int?> v) { var a = v.OfType<int>().ToList(); return a.Count == 0 ? null : Math.Round(a.Average(), 2); }
            var cur = Avg(ev.Select(x => x.CurrentLevel));
            var tgt = Avg(ev.Select(x => x.TargetLevel));
            return new NistReportProfile(code, name, fn, cur, tgt, cur is { } c && tgt is { } t ? Math.Round(t - c, 2) : null, l.Count,
                ev.Count, ev.Count, ev.Count, l.Count(x => x.State == "NotApplicable"), ev.Count, l.Count(x => x.State == "InProgress"),
                l.Count(x => x.State == "PendingConfirmation"), l.Count(x => x.State == "NotEvaluated"));
        }
        var functions = NistLabels.FunctionOrder.Select(f => Profile(f, NistLabels.FunctionName(f), null, subs.Where(x => x.FunctionCode == f))).ToList();
        var categories = subs.GroupBy(x => x.CategoryCode).Select(g => Profile(g.Key, g.Key, g.First().FunctionCode, g)).ToList();
        var evaluated = subs.Count(x => x.State == "Evaluated");
        var na = subs.Count(x => x.State == "NotApplicable");
        var withVal = functions.Where(f => f.Current is not null).ToList();
        var curAll = Math.Round(withVal.Average(f => f.Current!.Value), 2);
        var tgtAll = Math.Round(withVal.Average(f => f.Target!.Value), 2);
        var gaps = subs.Where(x => x.Gap is > 0).OrderByDescending(x => x.Gap).Select(x =>
            new NistReportGap(x.Code, x.Title, x.FunctionCode, x.CurrentLevel!.Value, x.TargetLevel!.Value, x.Gap!.Value, x.RiskImpact,
                findings.Count(f => f.SubcategoryCode == x.Code && f.Status == "Open"), null, "Sem tratamento")).ToList();
        var summary = new NistReportSummary(curAll, tgtAll, Math.Round(tgtAll - curAll, 2), subs.Count, evaluated, na,
            subs.Count(x => x.State == "InProgress"), subs.Count(x => x.State == "PendingConfirmation"), subs.Count(x => x.State == "NotEvaluated"),
            Math.Round(100.0 * (evaluated + na) / subs.Count, 1), evaluated, evaluated, evaluated, 0, evaluated, 0, 0, 0, 0, 0, 0,
            findings.Count(f => f.Status == "Open"), findings.Count(f => f.Status == "RiskAccepted"), findings.Count(f => f.Status == "Closed"),
            4, 0, 1, at);
        return new NistMaturityReport(NistMaturityReport.SchemaV1, new NistReportClient(ClientName),
            new NistReportAssessment(NistAssessment, "Avaliação anual sintética", null, "InProgress", new DateOnly(2025, 7, 1), null),
            new NistReportCycle(Guid.Parse("c0000000-0707-0707-0707-000000000003"), "T3 2026", "Quarterly", "Trimestral",
                new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30), "Open", "Julho 2026", "Reference", NistLabels.SeedMode("Reference")),
            new NistReportScope(NistScope, "Matriz", "Escopo sintético"),
            new NistReportCatalog("NIST CSF 2.0", 6, categories.Count, subs.Count),
            new NistReportMethodology("aegis-methodology-v1",
                Enumerable.Range(1, 5).Select(l => new NistReportLevel(l, $"Nível {l}", $"Descrição do nível {l}")).ToList(),
                NistMethodologyText.Statement, NistMethodologyText.AggregationRule, NistMethodologyText.CoverageRule, NistMethodologyText.InstrumentsNote),
            summary, functions, categories, subs, findings, gaps, new[] { "Dados sintéticos de verificação." },
            new NistReportPublication(PublishedAt, "Gestora Demo", new string('a', 64)));
    }
}
