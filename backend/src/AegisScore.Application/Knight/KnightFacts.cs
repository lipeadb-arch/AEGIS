using System;
using System.Collections.Generic;
using System.Linq;

namespace AegisScore.Application.Knight;

/// <summary>
/// Chave ESTÁVEL de um fato/observação normalizado do AEGIS KNIGHT. É o contrato tipado que desacopla as
/// regras determinísticas de qualquer fonte específica: um coletor (Demo, Entra, Google Workspace…) traduz
/// seus dados nestas chaves, e as regras leem por chave — sem conhecer Graph, Conditional Access ou o formato
/// de papéis/grupos de nenhum fornecedor. Ampliar a cobertura = acrescentar chaves aqui, sem `string` solta.
/// </summary>
public enum KnightSignalKey
{
    // ---- Compartilhados (avaliáveis tanto no Demo quanto em fontes reais) ----
    PrivilegedAccountsTotal = 0,
    PrivilegedAccountsWithoutMfa = 1,
    PrivilegedAccountsWithMailbox = 2,
    InactiveGuestAccounts = 3,
    ServiceAccountsMfaExempt = 4,
    /// <summary>Verdadeiro quando as isenções de MFA de contas de serviço têm controle compensatório COMPROVADO.</summary>
    ServiceAccountMfaExemptionProven = 5,

    // ---- Específicos de coleta real de diretório (Entra hoje; generalizáveis) ----
    /// <summary>Cobertura (%) de registro/capacidade de MFA entre os usuários habilitados.</summary>
    MfaRegistrationCoveragePercent = 100,
    /// <summary>Verdadeiro quando a autenticação legada está BLOQUEADA por política.</summary>
    LegacyAuthenticationBlocked = 101,
    /// <summary>Verdadeiro quando há política exigindo MFA para acesso administrativo.</summary>
    AdminMfaPolicyEnforced = 102,
    /// <summary>Credenciais (segredos/certificados) de aplicações vencidas ou vencendo na janela.</summary>
    ApplicationCredentialsExpiring = 103,
    /// <summary>Aplicações com permissões de aplicativo de ALTO privilégio sobre o diretório.</summary>
    HighPrivilegeApplications = 104,
    /// <summary>Contas privilegiadas sem sinal de atividade recente (obsoletas).</summary>
    StalePrivilegedAccounts = 105,
    /// <summary>Membros EXTERNOS (convidados) em papéis privilegiados.</summary>
    ExternalMembersInPrivilegedRoles = 106,
    /// <summary>Aplicações com consentimento administrativo amplo (tenant-wide).</summary>
    AdminConsentedApplications = 107,
    /// <summary>Verdadeiro quando os "security defaults" da plataforma estão habilitados.</summary>
    SecurityDefaultsEnabled = 108,
    /// <summary>Contas de emergência/break-glass designadas (requer marcação — normalmente não inferível por API).</summary>
    DesignatedBreakGlassAccounts = 109,
}

/// <summary>Desfecho de UMA observação: coletada, ausente (dado/permissão) ou não aplicável à fonte.</summary>
public enum KnightObservationOutcome
{
    /// <summary>O valor foi efetivamente coletado.</summary>
    Collected = 0,

    /// <summary>Faltou o dado ou a permissão para coletá-lo — reduz a cobertura, NUNCA aprova.</summary>
    Missing = 1,

    /// <summary>O sinal não se aplica a esta fonte/ambiente.</summary>
    NotApplicable = 2,
}

/// <summary>
/// Uma observação normalizada e TIPADA: a chave estável, o desfecho e o valor (contagem, razão ou flag).
/// A regra do indicador lê estas observações — nunca um DTO específico de fornecedor.
/// </summary>
public sealed record KnightObservation(
    KnightSignalKey Key,
    KnightObservationOutcome Outcome,
    long? Count = null,
    double? Ratio = null,
    bool? Flag = null,
    string? MissingReason = null)
{
    public bool IsCollected => Outcome == KnightObservationOutcome.Collected;

    public static KnightObservation OfCount(KnightSignalKey key, long count) =>
        new(key, KnightObservationOutcome.Collected, Count: count);

    public static KnightObservation OfRatio(KnightSignalKey key, double ratio) =>
        new(key, KnightObservationOutcome.Collected, Ratio: ratio);

    public static KnightObservation OfFlag(KnightSignalKey key, bool flag) =>
        new(key, KnightObservationOutcome.Collected, Flag: flag);

    public static KnightObservation MissingData(KnightSignalKey key, string reason) =>
        new(key, KnightObservationOutcome.Missing, MissingReason: reason);

    public static KnightObservation NotApplicable(KnightSignalKey key, string reason) =>
        new(key, KnightObservationOutcome.NotApplicable, MissingReason: reason);
}

/// <summary>
/// Conjunto VALIDADO de observações keyed por <see cref="KnightSignalKey"/> — a estrutura que as regras
/// determinísticas consomem. Não é um dicionário frágil de strings: as chaves são tipadas, a validação é
/// central (rejeita observação nula; a última observação de uma chave prevalece) e uma chave ausente devolve
/// uma observação <see cref="KnightObservationOutcome.Missing"/> explícita — o consumidor nunca recebe nulo.
/// </summary>
public sealed class KnightFactSet
{
    private readonly IReadOnlyDictionary<KnightSignalKey, KnightObservation> _byKey;

    public KnightFactSet(IEnumerable<KnightObservation> observations)
    {
        if (observations is null) throw new ArgumentNullException(nameof(observations));
        var map = new Dictionary<KnightSignalKey, KnightObservation>();
        foreach (var o in observations)
        {
            if (o is null) throw new ArgumentException("Observação nula não é permitida no conjunto de fatos.", nameof(observations));
            map[o.Key] = o;   // última prevalece — dedupe defensivo por chave
        }
        _byKey = map;
    }

    public static KnightFactSet Empty { get; } = new(Array.Empty<KnightObservation>());

    /// <summary>Observação da chave; se ausente, devolve <see cref="KnightObservationOutcome.Missing"/> explícito.</summary>
    public KnightObservation Get(KnightSignalKey key) =>
        _byKey.TryGetValue(key, out var o) ? o : KnightObservation.MissingData(key, "Sinal não coletado.");

    public bool Has(KnightSignalKey key) => _byKey.ContainsKey(key);

    public IReadOnlyCollection<KnightObservation> All => _byKey.Values.ToList();
}
