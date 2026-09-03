using AegisScore.Domain;

namespace AegisScore.Application.Services;

// ---- Leitura ----------------------------------------------------------------

/// <summary>
/// [AEGIS-MVP-ADMIN-LIFECYCLE-01] Um tenant no CATÁLOGO administrativo da plataforma. Projeção somente
/// leitura para a superfície de <c>PlatformAdmin</c>: id, nome, slug, estado e datas. Sem dados operacionais
/// do tenant (conectores, usuários, score) — este é o catálogo, não o painel interno de um cliente.
/// </summary>
public record TenantAdminSummary(
    Guid Id, string Name, string Slug, TenantStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

// ---- Mutação ----------------------------------------------------------------

/// <summary>
/// Desfecho de uma mutação administrativa de tenant. Recusas ESPERADAS viajam como VALOR (o boundary global
/// traduziria um throw num 500 opaco); só <see cref="TenantSecurityException"/> sobe. <see cref="Tenant"/> só
/// vem no sucesso.
/// </summary>
public enum TenantAdminMutationStatus
{
    /// <summary>Nome/estado aplicados (idempotente).</summary>
    Updated = 0,

    /// <summary>Tenant inexistente (404). Só alcançável por quem JÁ passou pela policy de plataforma.</summary>
    NotFound = 1,

    /// <summary>Nome de exibição ausente ou acima do teto (400).</summary>
    InvalidName = 2,

    /// <summary>
    /// A suspensão deixaria o PRÓPRIO administrador da plataforma sem NENHUM ambiente ativo em que possa
    /// reentrar e reverter — a plataforma ficaria sem forma administrativa válida de recuperação (409).
    /// </summary>
    SelfLockoutForbidden = 3,
}

/// <summary>Resultado da mutação. <paramref name="Tenant"/> traz o estado APÓS a escrita; <paramref name="Detail"/> explica a recusa.</summary>
public record TenantAdminMutationResult(
    TenantAdminMutationStatus Status, TenantAdminSummary? Tenant = null, string? Detail = null)
{
    public bool Succeeded => Status == TenantAdminMutationStatus.Updated;

    public static TenantAdminMutationResult Ok(TenantAdminSummary tenant) =>
        new(TenantAdminMutationStatus.Updated, tenant);

    public static TenantAdminMutationResult Rejected(TenantAdminMutationStatus status, string? detail = null) =>
        new(status, null, detail);
}

/// <summary>
/// Renomeia o NOME DE EXIBIÇÃO de um tenant. ⚠️ O <c>Slug</c> NÃO trafega e é IMUTÁVEL neste pacote: ele
/// participa de identidade operacional e de configurações de autorização/allowlist — alterá-lo em silêncio
/// seria uma mudança de segurança.
/// </summary>
public record RenameTenantCommand(Guid TenantId, string Name);

/// <summary>
/// Suspende ou reativa um tenant. <paramref name="ActorAccountId"/> é a PESSOA autenticada (claim
/// <c>account_id</c>), NUNCA vinda do corpo — alimenta a guarda de auto-lockout da suspensão.
/// </summary>
public record SetTenantStatusCommand(Guid TenantId, bool Suspend, Guid ActorAccountId);

// ---- Porta ------------------------------------------------------------------

/// <summary>
/// [AEGIS-MVP-ADMIN-LIFECYCLE-01] Superfície de PLATAFORMA para o ciclo de vida dos tenants: catálogo,
/// renomeação do nome de exibição, suspensão e reativação. Autoridade EXCLUSIVA de <c>PlatformAdmin</c> (a
/// policy global barra qualquer papel de tenant na borda HTTP). É a contrapartida administrativa de
/// <see cref="ITenantManagementService.CreateTenantAsync"/> (provisionamento).
///
/// <b>Fronteiras deliberadas.</b> NÃO altera o slug (imutável), NÃO exclui fisicamente tenant (histórico
/// preservado), e NÃO toca score, ledger nem NIST. Suspender preserva histórico e configurações e apenas
/// IMPEDE o uso operacional: os fluxos de login/seletor/troca já excluem tenants suspensos, e a suspensão
/// revoga os refresh tokens ativos do ambiente (as sessões não sobrevivem à suspensão).
/// </summary>
public interface IPlatformTenantAdminService
{
    /// <summary>Catálogo COMPLETO de tenants (inclusive suspensos), em ordem estável por nome. Somente leitura.</summary>
    Task<IReadOnlyList<TenantAdminSummary>> ListTenantsAsync(CancellationToken ct = default);

    /// <summary>
    /// Aplica um novo nome de exibição a um tenant. O slug é IMUTÁVEL (não é parâmetro). Nome inválido → 400;
    /// tenant inexistente → 404. Idempotente.
    /// </summary>
    Task<TenantAdminMutationResult> RenameTenantAsync(RenameTenantCommand command, CancellationToken ct = default);

    /// <summary>
    /// Suspende (<c>Suspend=true</c>) ou reativa (<c>Suspend=false</c>) um tenant. Suspender:
    /// <list type="bullet">
    /// <item>define <see cref="TenantStatus.Suspended"/> e revoga os refresh tokens ativos do ambiente;</item>
    /// <item>é BARRADO quando deixaria o próprio ator sem ambiente ativo de recuperação
    /// (<see cref="TenantAdminMutationStatus.SelfLockoutForbidden"/>), correto sob concorrência (trava de linha
    /// nas linhas de acesso ativas do ator no PostgreSQL).</item>
    /// </list>
    /// Reativar leva <see cref="TenantStatus.Suspended"/> de volta a <see cref="TenantStatus.Active"/> (não
    /// restaura sessões antigas). Ambos idempotentes.
    /// </summary>
    Task<TenantAdminMutationResult> SetTenantStatusAsync(SetTenantStatusCommand command, CancellationToken ct = default);
}
