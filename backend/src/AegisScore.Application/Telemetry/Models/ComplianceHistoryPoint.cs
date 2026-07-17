namespace AegisScore.Application.Telemetry.Models;

/// <summary>
/// Um ponto da série de conformidade de UM controle — a matéria-prima da sparkline de 30 dias no card,
/// que responde "este controle está melhorando ou apodrecendo?".
///
/// Distinto de <c>TenantTrendDto</c> (a foto AGREGADA do tenant inteiro, que alimenta o HUD <c>/trend</c>
/// e é produzida pelo <c>AegisScoreSnapshotWorker</c>): aqui a granularidade é a célula tenant ×
/// subcategoria. ⚠️ Essa série ainda NÃO tem produtor — não existe snapshot por controle no banco, só o
/// agregado diário do tenant. O contrato existe para a leitura já entregá-la quando o snapshot por célula
/// for implementado; até lá trafega VAZIA, e a sparkline se omite. Preencher com dado sintético seria
/// forjar histórico de conformidade.
/// </summary>
/// <param name="Date">Dia lógico da foto (sem hora, sem fuso) — mesma convenção de <c>TenantScoreSnapshot</c>.</param>
/// <param name="CompliancePercent">Percentual da célula no dia (0–100): CurrentScore / MaxScorePoints.</param>
public record ComplianceHistoryPoint(DateOnly Date, int CompliancePercent);
