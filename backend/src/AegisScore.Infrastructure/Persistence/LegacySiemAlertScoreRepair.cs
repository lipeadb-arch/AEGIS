using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using AegisScore.Domain;
using AegisScore.Infrastructure.Connectors;
using AegisScore.Infrastructure.Documents;
using AegisScore.Infrastructure.Scoring;

namespace AegisScore.Infrastructure.Persistence;

/// <summary>
/// [AEGIS-MVP-SCORE-GUARD-SIEM-01] Reparo CONSERVADOR e IDEMPOTENTE das projeções de score que o mapping
/// aposentado (SIEM alerta de alta severidade → Compliant) possa ter produzido no ledger. Executado pelo
/// <c>AegisScore.DbMigrator</c> DEPOIS de o seed aposentar o mapping (<see cref="RetiredSiemAlertMapping"/>).
///
/// ⚠️ O <c>TenantControlState</c> NÃO registra o <c>EvidenceSignal</c> de origem — logo NÃO há como afirmar
/// "este estado veio daquele alerta". Por isso a estratégia é conservadora e SEGURA por construção: para cada
/// controle historicamente afetado (<see cref="RetiredSiemAlertMapping.AffectedControls"/>) cujo estado VIGENTE
/// seja de TELEMETRIA, num tenant que possua o sinal legado, RECOMPUTA o veredito determinístico a partir de
/// TODOS os sinais que continuam válidos (a autoridade ÚNICA <see cref="EvidenceTelemetryRecompute"/>, com o
/// mapping já removido):
///   • se OUTRA evidência válida ainda sustenta um veredito → reaplica-o (o controle NÃO perde um veredito
///     realmente sustentado — pode inclusive continuar Compliant por EDR/Secure Score);
///   • se NENHUMA evidência válida remanescente sustenta → RETRAI o estado telemétrico (volta a "não avaliado");
///     NUNCA converte ausência de prova em NonCompliant; e reusa o reconciliador DOCUMENTAL existente para que
///     um crédito documental elegível reapareça (sem duplicar regras de elegibilidade/teto).
///
/// Preserva: os EvidenceSignals legados (registro auditável), estados documentais, outros controles, outros
/// mappings, outros tenants e os dados de conector. Tenant-safe (query filter + SystemTenantContext), transacional
/// por tenant, determinístico e reparável por reexecução (a 2ª execução não encontra estado telemétrico inflado).
/// </summary>
public static class LegacySiemAlertScoreRepair
{
    /// <summary>
    /// Executa o reparo em TODOS os tenants que possuam o sinal legado. Devolve o total de estados
    /// reprojetados/retraídos (para diagnóstico). Idempotente: sem nada a reparar, é no-op.
    /// </summary>
    public static async Task<int> RepairAsync(
        DbContextOptions<AegisScoreDbContext> options, ILoggerFactory logs, CancellationToken ct = default)
    {
        var log = logs.CreateLogger("AegisScore.LegacySiemAlertScoreRepair");

        // Descoberta CROSS-TENANT (contexto de sistema explícito + IgnoreQueryFilters): SÓ tenants que possuem o
        // sinal legado (capability SIEM + signalKey aposentado). Nenhum outro tenant é alcançado.
        List<Guid> tenantIds;
        await using (var scan = new AegisScoreDbContext(options, new SystemTenantContext(null)))
        {
            tenantIds = await (
                from s in scan.Signals.IgnoreQueryFilters()
                join c in scan.Connectors.IgnoreQueryFilters() on s.ConnectorConfigId equals c.Id
                where s.SignalKey == RetiredSiemAlertMapping.SignalKey
                    && c.Capability == RetiredSiemAlertMapping.Capability
                select s.TenantId).Distinct().ToListAsync(ct);
        }

        if (tenantIds.Count == 0)
        {
            log.LogInformation("Reparo SCORE-GUARD-SIEM-01: nenhum tenant com sinal legado — nada a reparar.");
            return 0;
        }

        var total = 0;
        foreach (var tenantId in tenantIds)
            total += await RepairTenantAsync(options, logs, tenantId, ct);

        log.LogInformation(
            "Reparo SCORE-GUARD-SIEM-01: {Tenants} tenant(s) com sinal legado; {Total} estado(s) telemétrico(s) reprojetado(s)/retraído(s).",
            tenantIds.Count, total);
        return total;
    }

    private static async Task<int> RepairTenantAsync(
        DbContextOptions<AegisScoreDbContext> options, ILoggerFactory logs, Guid tenantId, CancellationToken ct)
    {
        var tenantCtx = new SystemTenantContext(tenantId);
        await using var db = new AegisScoreDbContext(options, tenantCtx);

        var affected = RetiredSiemAlertMapping.AffectedControls;

        // Ids das subcategorias afetadas (catálogo global, imutável) e o mapa id→código.
        var subIdToCode = await db.Subcategories
            .Where(s => affected.Contains(s.Code))
            .ToDictionaryAsync(s => s.Id, s => s.Code, ct);
        if (subIdToCode.Count == 0) return 0;
        var affectedSubIds = subIdToCode.Keys.ToList();

        // Estados VIGENTES dos controles afetados cuja fonte é TELEMETRIA (rastreados, para reaplicar/remover).
        var states = await db.TenantControlStates
            .Where(t => affectedSubIds.Contains(t.SubcategoryId) && t.LastVerdictSource == VerdictSource.Telemetry)
            .ToListAsync(ct);
        if (states.Count == 0) return 0;

        var codes = states.Select(t => subIdToCode[t.SubcategoryId]).Distinct().ToList();

        // Transação por tenant (atômica no PostgreSQL; SQLite também suporta). Migrator é single-thread sob advisory
        // lock — sem concorrência, o writer não entra no caminho de corrida de inserção.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var mapper = new NistSignalMapper(db);
        var writer = new ControlStateWriter(db, tenantCtx, logs.CreateLogger<ControlStateWriter>());
        var docReconciler = new DocumentEvidenceReconciler(db, tenantCtx, writer, logs.CreateLogger<DocumentEvidenceReconciler>());

        // Recompute-from-newest GLOBAL com o mapping já removido: o veredito que a evidência REMANESCENTE sustenta.
        var verdicts = await new EvidenceTelemetryRecompute(db, mapper).ComputeAsync(codes, ct);

        var changed = 0;
        var toRetract = new List<string>();
        foreach (var state in states)
        {
            var code = subIdToCode[state.SubcategoryId];
            if (verdicts.TryGetValue(code, out var verdict))
            {
                // Outra evidência válida ainda sustenta um veredito → reaplica SÓ se o estado atual diverge
                // (idempotente: reexecução não reescreve um estado já correto).
                if (state.Status != verdict.Status)
                {
                    await writer.ApplyVerdictAsync(
                        tenantId, code, verdict.Status, verdict.Reason, VerdictSource.Telemetry, ct: ct);
                    changed++;
                }
            }
            else
            {
                toRetract.Add(code);
            }
        }

        // Retração dos estados telemétricos sem evidência válida remanescente: remove a linha (volta a "não
        // avaliado"), NUNCA NonCompliant. Depois reusa o reconciliador documental (reaparece crédito documental
        // elegível; no-op se não houver). O escritor documental preserva telemetria — daí a retração vir ANTES.
        if (toRetract.Count > 0)
        {
            db.TenantControlStates.RemoveRange(states.Where(s => toRetract.Contains(subIdToCode[s.SubcategoryId])));
            await db.SaveChangesAsync(ct);
            changed += toRetract.Count;

            await docReconciler.ReconcileAsync(tenantId, toRetract, ct);
        }

        await tx.CommitAsync(ct);
        return changed;
    }
}
