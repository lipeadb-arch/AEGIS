using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Domain;

namespace AegisScore.Application.Knight;

/// <summary>
/// Capacidade lógica de coleta de uma fonte (um grupo de consultas com uma permissão associada). O estado por
/// capacidade explicita o que foi obtido e o que faltou — base da cobertura e das "limitações" na UI e na IA.
/// </summary>
public enum KnightCapability
{
    /// <summary>Inventário de papéis/atribuições privilegiadas.</summary>
    PrivilegedRoleInventory = 0,

    /// <summary>Cobertura/registro de MFA dos usuários.</summary>
    MfaRegistration = 1,

    /// <summary>Contas de convidado e sua atividade.</summary>
    GuestAccounts = 2,

    /// <summary>Políticas de acesso condicional / baseline.</summary>
    ConditionalAccessPolicies = 3,

    /// <summary>Inventário de aplicações (credenciais e permissões).</summary>
    ApplicationInventory = 4,

    /// <summary>Isenções de MFA de contas de serviço e seus controles compensatórios.</summary>
    ServiceAccountExemptions = 5,

    /// <summary>Configuração de segurança padrão da plataforma.</summary>
    SecurityBaseline = 6,

    /// <summary>Designação de contas de emergência/break-glass.</summary>
    BreakGlassDesignation = 7,
}

/// <summary>Desfecho da coleta de UMA capacidade.</summary>
public enum KnightCapabilityOutcome
{
    Collected = 0,
    InsufficientPermission = 1,
    Unavailable = 2,
    NotAttempted = 3,
}

/// <summary>Estado por capacidade — o que a fonte conseguiu (ou não) coletar, com detalhe sanitizado.</summary>
public sealed record KnightCapabilityStatus(KnightCapability Capability, KnightCapabilityOutcome Outcome, string? Detail = null);

// ---- Configuração de fonte (tipada por fonte; sem dicionário de strings) --------------------------------

/// <summary>Configuração RESOLVIDA de uma fonte para um tenant. Subtipos tipados por fonte; nunca um dict solto.</summary>
public abstract record KnightSourceConfiguration
{
    public abstract KnightSourceType Source { get; }
    public bool IsConfigured => this is not KnightSourceNotConfigured;
}

/// <summary>Não há configuração aplicável para a fonte neste tenant.</summary>
public sealed record KnightSourceNotConfigured(KnightSourceType SourceType) : KnightSourceConfiguration
{
    public override KnightSourceType Source => SourceType;
}

/// <summary>Fonte de demonstração (sem credenciais — dados sintéticos).</summary>
public sealed record KnightDemoConfiguration : KnightSourceConfiguration
{
    public override KnightSourceType Source => KnightSourceType.Demo;
}

/// <summary>
/// Configuração do coletor real do Microsoft Entra ID — client credentials por tenant (segredo DECIFRADO em
/// memória, nunca persistido/logado aqui). Base de login/Graph são injetáveis para teste (HTTP simulado).
/// </summary>
public sealed record KnightEntraIdConfiguration(
    string AzureTenantId,
    string ClientId,
    string ClientSecret,
    string? GraphBaseUrl = null,
    string? LoginBaseUrl = null) : KnightSourceConfiguration
{
    public override KnightSourceType Source => KnightSourceType.MicrosoftEntraId;
}

// ---- Coletor -------------------------------------------------------------------------------------------

/// <summary>Contexto de uma coleta: o tenant e a configuração RESOLVIDA da fonte.</summary>
public sealed record KnightCollectionContext(Guid TenantId, KnightSourceConfiguration Configuration)
{
    public KnightSourceType Source => Configuration.Source;
}

/// <summary>
/// Resultado NORMALIZADO de uma coleta: fatos tipados + estado da fonte + estado por capacidade. As regras
/// determinísticas avaliam os fatos; a fonte é sempre identificada. Dados não obtidos ficam Missing (viram
/// NotEvaluated na avaliação), nunca aprovação.
/// </summary>
public sealed record KnightCollectionResult(
    KnightSourceType Source,
    KnightSourceState State,
    string SourceLabel,
    KnightFactSet Facts,
    IReadOnlyList<KnightCapabilityStatus> Capabilities,
    DateTimeOffset CollectedAt,
    string? Detail = null)
{
    public static KnightCollectionResult NotConfigured(KnightSourceType source, string label) => new(
        source, KnightSourceState.NotConfigured, label, KnightFactSet.Empty,
        Array.Empty<KnightCapabilityStatus>(), DateTimeOffset.UtcNow, "Fonte não configurada.");
}

/// <summary>
/// Coletor do AEGIS KNIGHT: colhe de UMA fonte e devolve fatos normalizados + estado. O núcleo só conhece
/// esta porta — acrescentar uma fonte (Google Workspace, AD, Okta…) é registrar outro coletor, sem refatorar.
/// </summary>
public interface IKnightCollector
{
    KnightSourceType Source { get; }
    Task<KnightCollectionResult> CollectAsync(KnightCollectionContext context, CancellationToken ct = default);
}

/// <summary>Registro/factory de coletores por <see cref="KnightSourceType"/>.</summary>
public interface IKnightCollectorRegistry
{
    bool TryResolve(KnightSourceType source, out IKnightCollector? collector);
    IKnightCollector Resolve(KnightSourceType source);
    IReadOnlyList<KnightSourceType> RegisteredSources { get; }
}

/// <summary>Disponibilidade de uma fonte real para um tenant (para a UI escolher Demo × real).</summary>
public sealed record KnightSourceAvailability(KnightSourceType Source, string Label, bool Configured, bool Enabled);

/// <summary>
/// Resolve a CONFIGURAÇÃO de uma fonte para um tenant — lê o <c>ConnectorConfig</c> e DECIFRA os segredos
/// (Infrastructure). Demo é sempre disponível; fontes reais dependem de configuração por tenant.
/// </summary>
public interface IKnightSourceConfigurationProvider
{
    Task<KnightSourceConfiguration> ResolveAsync(Guid tenantId, KnightSourceType source, CancellationToken ct = default);
    Task<IReadOnlyList<KnightSourceAvailability>> ListAvailabilityAsync(Guid tenantId, CancellationToken ct = default);
}
