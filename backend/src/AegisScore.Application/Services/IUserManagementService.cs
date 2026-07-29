using AegisScore.Domain;

namespace AegisScore.Application.Services;

// ---- Comando de entrada -----------------------------------------------------

/// <summary>
/// [AEGIS-AUD-010] Concessão de acesso ao tenant AMBIENTE a uma identidade global JÁ EXISTENTE.
///
/// A chave de descoberta é o <paramref name="IdentityAccountId"/> — a chave estrangeira da
/// <see cref="IdentityAccount"/>, NUNCA o e-mail: casar por string era o vetor que a identidade global
/// fechou. Não há <c>TenantId</c> aqui de propósito: o tenant vem do claim <c>tenant_id</c> do JWT e é
/// revalidado no carimbo de gravação — o que não trafega não pode ser forjado para rotear a escrita a
/// outro ambiente. Também não há senha/InitialPassword: esta autoridade não toca credencial global.
/// </summary>
/// <param name="DisplayName">Nome exibido NESTE cliente (a mesma pessoa pode se apresentar diferente em cada um).</param>
/// <param name="Role">Papel TENANT-SCOPED exercido NESTE tenant (<see cref="TenantRole"/>). O eixo global
/// (<c>PlatformAdmin</c>) não existe neste tipo e não é atribuível por esta superfície.</param>
public record GrantTenantAccessCommand(Guid IdentityAccountId, string DisplayName, TenantRole Role);

// ---- Resultado de saída -----------------------------------------------------

/// <summary>
/// Desfecho da concessão. Como na §20/§21, conflito e validação são resultados ESPERADOS do fluxo (→ na
/// borda) e viajam como VALOR: o <c>GlobalExceptionHandlingMiddleware</c> traduziria qualquer throw num
/// 500 opaco. Só <see cref="TenantSecurityException"/> sobe.
/// </summary>
public enum AccessGrantStatus
{
    /// <summary>Novo membership criado no tenant ambiente.</summary>
    Granted = 0,

    /// <summary>Membership já existia neste tenant; papel/nome/estado atualizados (idempotente, reativa se inativa).</summary>
    AccessUpdated = 1,

    /// <summary>
    /// Nenhuma identidade global com o <c>IdentityAccountId</c> informado. Resposta GENÉRICA — a identidade
    /// NÃO é criada aqui (o provisionamento global é a autoridade separada do <see cref="IIdentityProvisioningService"/>).
    /// </summary>
    IdentityNotFound = 2,

    /// <summary>Nome de exibição ausente ou acima de 200 caracteres.</summary>
    InvalidDisplayName = 3,

    /// <summary>Papel não atribuível por esta superfície — ver a nota de escalonamento na interface.</summary>
    RoleNotAssignable = 4,
}

/// <summary>Projeção SEGURA de um membership. NUNCA carrega <c>PasswordHash</c> (a credencial é da pessoa).</summary>
public record UserSummary(
    Guid Id,
    Guid TenantId,
    string Email,
    string DisplayName,
    TenantRole Role,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);

/// <summary>
/// Resultado da concessão. <paramref name="User"/> só vem preenchido no sucesso; <paramref name="Detail"/>
/// explica a recusa de validação (a política vive no serviço, não na borda — duplicá-la garantiria divergência).
/// </summary>
public record AccessGrantResult(
    AccessGrantStatus Status, UserSummary? User = null, string? Detail = null)
{
    public bool Succeeded =>
        Status is AccessGrantStatus.Granted or AccessGrantStatus.AccessUpdated;

    public static AccessGrantResult Ok(AccessGrantStatus status, UserSummary user) =>
        new(status, user);

    public static AccessGrantResult Rejected(AccessGrantStatus status, string? detail = null) =>
        new(status, null, detail);
}

// ---- Porta ------------------------------------------------------------------

/// <summary>
/// [AEGIS-AUD-010] Serviço de aplicação de CONCESSÃO DE ACESSO A TENANT. Cria/atualiza o <see cref="User"/>
/// (membership) SEMPRE dentro do tenant ambiente. É a autoridade tenant-scoped, SEPARADA do provisionamento
/// global de identidade (<see cref="IIdentityProvisioningService"/>): esta superfície NÃO cria
/// <see cref="IdentityAccount"/>, não toca <c>PasswordHash</c> nem o vínculo Entra
/// (<c>ExternalTenantId</c>/<c>ExternalObjectId</c>), e não descobre identidades por e-mail.
///
/// <b>Modelo de vínculo — Um-para-Muitos.</b> <see cref="User"/> é <see cref="ITenantOwned"/> com UM
/// <c>TenantId</c>; a mesma <see cref="IdentityAccount"/> (global) pode ter um membership por tenant, e o
/// índice único é <c>(TenantId, IdentityAccountId)</c>. É o que impede que qualquer token atravesse a
/// fronteira: não existe sujeito capaz de "trocar de tenant" sem um membership ativo lá.
///
/// <b>A identidade global deve PREEXISTIR.</b> O fluxo canônico é: o <c>PlatformAdmin</c> provisiona a
/// identidade (por e-mail) pela superfície global; depois o <c>TenantAdmin</c> concede o acesso usando o
/// <c>IdentityAccountId</c> devolvido. Um <c>IdentityAccountId</c> inexistente devolve
/// <see cref="AccessGrantStatus.IdentityNotFound"/> — nada é criado.
///
/// <b>Nada aqui fura o isolamento.</b> O tenant é o ambiente (claim do JWT), revalidado pelo
/// <c>StampTenant</c> fail-closed; um <c>TenantAdmin</c> nunca escreve noutro tenant. Sem leitura
/// cross-tenant de membership, sem <c>IgnoreQueryFilters</c>.
///
/// <b>⚠️ Só o eixo TENANT.</b> [AEGIS-AUD-011] O <see cref="GrantTenantAccessCommand.Role"/> é
/// <see cref="TenantRole"/> — a autoridade global (<c>PlatformAdmin</c>) nem sequer existe neste tipo, então
/// esta superfície não pode concedê-la. A validação é uma allowlist (Analyst/Manager/TenantAdmin); um valor
/// indefinido do enum devolve <see cref="AccessGrantStatus.RoleNotAssignable"/>. A concessão de membership
/// NUNCA altera o <see cref="IdentityAccount.PlatformRole"/> (o eixo global vive na identidade).
/// </summary>
public interface IUserManagementService
{
    /// <summary>
    /// Concede acesso ao tenant ambiente de forma IDEMPOTENTE a uma identidade global preexistente:
    /// <list type="bullet">
    /// <item>identidade ausente → <see cref="AccessGrantStatus.IdentityNotFound"/> (nada é criado);</item>
    /// <item>sem membership aqui → cria e devolve <see cref="AccessGrantStatus.Granted"/>;</item>
    /// <item>com membership → aplica papel e nome, REATIVA se estava inativo, e devolve
    /// <see cref="AccessGrantStatus.AccessUpdated"/>. A credencial global e o vínculo Entra são preservados:
    /// conceder acesso não é resetar credencial nem revincular identidade.</item>
    /// </list>
    /// A unicidade <c>(TenantId, IdentityAccountId)</c> é invariante de BANCO: a checagem prévia é fast-path
    /// e a corrida perdida no INSERT reconcilia no membership vencedor (idempotente).
    /// </summary>
    /// <exception cref="TenantSecurityException">Sem tenant resolvido no contexto (fail-closed).</exception>
    Task<AccessGrantResult> GrantAccessAsync(
        GrantTenantAccessCommand command, CancellationToken ct = default);
}
