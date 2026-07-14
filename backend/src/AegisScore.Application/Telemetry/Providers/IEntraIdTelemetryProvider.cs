using AegisScore.Application.Telemetry.Models;

namespace AegisScore.Application.Telemetry.Providers;

/// <summary>
/// Leitura CRUA da postura de identidade colhida do Microsoft Entra ID pelo conector — o DTO de retorno
/// da porta <see cref="IEntraIdTelemetryProvider"/>. Carrega as métricas de exposição mais os metadados da
/// coleta (tenant, domínio ecoado, carimbo temporal) que o sinal normalizado não precisa transportar. É a
/// forma "de borda" (específica do fornecedor); o núcleo do Aegis consome a forma normalizada
/// <see cref="IdentityTelemetrySignal"/>, obtida por <see cref="ToTelemetrySignal"/>. Mesmo idioma do
/// par <c>DocumentDto</c> (borda) → pipeline documental.
/// </summary>
/// <param name="TenantId">Tenant avaliado (o mesmo resolvido do contexto na borda — nunca confiado do corpo).</param>
/// <param name="TenantDomain">Domínio primário do tenant no Entra (ex.: "vicunha.com.br"), ecoado para rastreabilidade.</param>
/// <param name="TotalPrivilegedAccounts">Total de contas privilegiadas descobertas.</param>
/// <param name="PrivilegedAccountsWithoutMfa">Quantas dessas contas privilegiadas estão sem MFA efetivo.</param>
/// <param name="PrivilegedAccountsWithMailbox">Quantas contas privilegiadas têm caixa de correio ativa.</param>
/// <param name="InactiveGuestAccountsOver30Days">Convidados inativos há mais de 30 dias.</param>
/// <param name="MfaExemptServiceAccounts">Contas de serviço/OT que não suportam MFA por natureza técnica.</param>
/// <param name="CollectedAt">Instante da coleta (UTC).</param>
public record EntraIdIdentityPosture(
    Guid TenantId,
    string TenantDomain,
    int TotalPrivilegedAccounts,
    int PrivilegedAccountsWithoutMfa,
    int PrivilegedAccountsWithMailbox,
    int InactiveGuestAccountsOver30Days,
    IReadOnlyList<string> MfaExemptServiceAccounts,
    DateTimeOffset CollectedAt)
{
    /// <summary>
    /// Mapeia a leitura crua do Entra no sinal de telemetria NORMALIZADO e agnóstico de fornecedor que o
    /// motor avalia. É o único ponto de tradução conector → núcleo: nada além dele conhece o formato do Entra.
    /// O CONTEXTO DE REDE (isolamento/controles compensatórios) é ENXERTADO aqui pelo chamador (o
    /// controller), pois o Entra não o conhece — é conhecimento de infraestrutura. Sem ele, o retrato é
    /// avaliado sem atenuantes (fail-closed: contas privilegiadas sem MFA reprovam).
    /// </summary>
    public IdentityTelemetrySignal ToTelemetrySignal(
        bool hasNetworkIsolation = false, IReadOnlyList<string>? compensatingControls = null) => new(
        TotalPrivilegedAccounts,
        PrivilegedAccountsWithoutMfa,
        PrivilegedAccountsWithMailbox,
        InactiveGuestAccountsOver30Days,
        MfaExemptServiceAccounts,
        hasNetworkIsolation,
        compensatingControls);
}

/// <summary>
/// Porta (Provider Pattern) para colher a postura de identidade do Microsoft Entra ID. Vive na
/// APPLICATION — e não no pacote Connectors.Microsoft — de propósito: é o contrato que o núcleo consome, e
/// a regra de dependência da Clean Architecture proíbe a Application de referenciar uma camada de conector.
/// A implementação (stub agora; OAuth client credentials + Microsoft Graph depois) vive em
/// Connectors.Microsoft. Espelha exatamente <see cref="AegisScore.Application.Services.IDocumentIntegrationProvider"/>
/// (porta na Application) ↔ <c>SharePointProvider</c> (impl no conector).
/// </summary>
public interface IEntraIdTelemetryProvider
{
    /// <summary>
    /// Puxa da fonte (Graph) o retrato de postura de identidade do tenant. Contrato: falha apenas em erro
    /// real de transporte/credencial; a normalização para <see cref="IdentityTelemetrySignal"/> e a
    /// avaliação por controle (PR.AA-01, GV.RR-01) são responsabilidade do chamador (futuro serviço de
    /// ingestão), não desta porta.
    /// </summary>
    /// <param name="tenantId">Tenant alvo (deve casar com o tenant do contexto — defesa em profundidade no chamador).</param>
    /// <param name="tenantDomain">Domínio primário do tenant no Entra, ex.: "vicunha.com.br".</param>
    Task<EntraIdIdentityPosture> FetchIdentityPostureAsync(
        Guid tenantId, string tenantDomain, CancellationToken ct = default);
}
