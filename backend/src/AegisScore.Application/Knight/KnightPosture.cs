using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Domain;

namespace AegisScore.Application.Knight;

/// <summary>
/// Um controle compensatório COMPROVADO presente no snapshot do provedor — a ÚNICA base admissível para um
/// veredito <see cref="KnightIndicatorStatus.Mitigated"/>. Deliberadamente NÃO é um toggle de UI: um botão
/// visual jamais rebaixa a exposição de Exposed para Mitigated; só uma evidência técnica no snapshot o faz.
/// </summary>
/// <param name="ControlKey">Chave estável do controle (ex.: "service-account-network-isolation").</param>
/// <param name="Description">Evidência factual e legível do controle (o que prova sua existência), sem PII.</param>
/// <param name="CoversCategory">Categoria de exposição que este controle compensa.</param>
/// <param name="TechnicallyProven">
/// TRUE somente quando há prova técnica (ex.: exportação de configuração verificada). Um controle
/// declarado porém NÃO comprovado não sustenta a mitigação — a regra o ignora (fail-closed).
/// </param>
public sealed record KnightCompensatingControl(
    string ControlKey,
    string Description,
    KnightIndicatorCategory CoversCategory,
    bool TechnicallyProven);

/// <summary>
/// Retrato de postura de identidade e exposição coletado pelo provedor KNIGHT. Na modalidade
/// <see cref="KnightAssessmentMode.Demo"/> os números são 100% SINTÉTICOS (domínio example.com) e
/// reprodutíveis — o provedor NUNCA consultou Microsoft Graph, AD local ou Okta. As próximas entregas
/// preencherão o mesmo contrato a partir de fontes reais, sem mudar o motor determinístico que o consome.
/// </summary>
/// <param name="Mode">Demo (sintético) ou Live (real).</param>
/// <param name="Source">Rótulo legível da fonte da coleta — nunca uma marca de terceiro.</param>
/// <param name="TenantDomain">Domínio ecoado do ambiente avaliado (no Demo, sempre "demo.example.com").</param>
/// <param name="CollectedAt">Instante da coleta (UTC).</param>
/// <param name="TotalPrivilegedAccounts">Total de contas privilegiadas descobertas.</param>
/// <param name="PrivilegedAccountsWithoutMfa">Contas privilegiadas sem MFA efetivo.</param>
/// <param name="PrivilegedAccountsWithMailbox">Contas privilegiadas com caixa de correio ativa.</param>
/// <param name="InactiveGuestAccountsOverWindow">Convidados inativos além da janela de <paramref name="InactiveGuestWindowDays"/> dias.</param>
/// <param name="InactiveGuestWindowDays">Janela (dias) que define "convidado inativo".</param>
/// <param name="MfaExemptServiceAccounts">Contas técnicas/de serviço isentas de MFA (identificadores fictícios no Demo).</param>
/// <param name="CompensatingControls">Controles compensatórios COMPROVADOS — base admissível para Mitigated.</param>
public sealed record KnightPostureSnapshot(
    KnightAssessmentMode Mode,
    string Source,
    string TenantDomain,
    DateTimeOffset CollectedAt,
    int TotalPrivilegedAccounts,
    int PrivilegedAccountsWithoutMfa,
    int PrivilegedAccountsWithMailbox,
    int InactiveGuestAccountsOverWindow,
    int InactiveGuestWindowDays,
    IReadOnlyList<string> MfaExemptServiceAccounts,
    IReadOnlyList<KnightCompensatingControl> CompensatingControls);

/// <summary>
/// Porta (Provider Pattern) que colhe o snapshot de postura para o AEGIS KNIGHT. A implementação de
/// demonstração sintetiza dados fictícios; as futuras implementações reais (Microsoft Graph, AD, Okta)
/// preencherão o MESMO contrato. Vive na Application — o núcleo consome a porta; a impl mora na Infrastructure,
/// respeitando a regra de dependência da Clean Architecture (espelha <c>IEntraIdTelemetryProvider</c>).
/// </summary>
public interface IKnightPostureProvider
{
    /// <summary>Modo desta implementação (Demo/Live) — refletido na execução e rotulado como DEMONSTRAÇÃO na UI.</summary>
    KnightAssessmentMode Mode { get; }

    /// <summary>Colhe o snapshot de postura do tenant. No Demo, retorno determinístico e sem rede.</summary>
    Task<KnightPostureSnapshot> CollectAsync(Guid tenantId, CancellationToken ct = default);
}
