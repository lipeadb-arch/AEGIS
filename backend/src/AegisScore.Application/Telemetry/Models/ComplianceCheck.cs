namespace AegisScore.Application.Telemetry.Models;

/// <summary>
/// Um item do CHECKLIST técnico que justifica o veredito de um controle — a decomposição auditável do
/// status em verificações objetivas (ex.: "Endpoint Encrypted", "No Critical Patches Pending"). O motor
/// (StubLlmClient / IA real) popula a lista ao avaliar as métricas; ela viaja no <c>ComplianceVerdict</c>,
/// é persistida com o estado do controle e o HUD a expande no card (accordion). Transparência: o analista
/// vê EXATAMENTE qual condição passou (✓) ou falhou (✕), não só o rótulo agregado.
/// </summary>
/// <param name="Name">Rótulo curto e legível da verificação (ex.: "Endpoint Encrypted").</param>
/// <param name="Passed">Resultado objetivo: a condição foi satisfeita?</param>
/// <param name="Details">Justificativa com o valor concreto que decidiu o check (ex.: "Cobertura em 90% (mínimo 95%).").</param>
public record ComplianceCheck(string Name, bool Passed, string Details);
