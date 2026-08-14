using AegisScore.Domain;

namespace AegisScore.Application.Services;

/// <summary>
/// Autoridade ÚNICA das regras de validação tenant-scoped compartilhadas pela concessão de acesso
/// (<see cref="IUserManagementService"/>) e pelo onboarding de plataforma
/// (<see cref="IPlatformTenantUserService"/>). Extraída para que as duas superfícies apliquem EXATAMENTE
/// a mesma allowlist de papéis e o mesmo limite de nome — divergir aqui abriria um caminho para gravar um
/// papel que a outra recusa.
/// </summary>
public static class TenantAccessPolicy
{
    /// <summary>Teto do nome de exibição — espelha o <c>HasMaxLength(200)</c> da coluna <c>User.DisplayName</c>.</summary>
    public const int MaxDisplayNameLength = 200;

    /// <summary>
    /// [AEGIS-AUD-011] ALLOWLIST explícita dos papéis TENANT-SCOPED atribuíveis. A autoridade global
    /// (<c>PlatformAdmin</c>) NÃO existe em <see cref="TenantRole"/>, então não há o que comparar — o
    /// escalonamento é barrado pelo TIPO. O que esta lista guarda são valores INDEFINIDOS do enum: o ASP.NET
    /// Core desserializa enum de número, então <c>"role": 999</c> chega como <c>(TenantRole)999</c>, que uma
    /// checagem por desigualdade deixaria passar e corromperia o membership. Só 0/1/2 são aceitos.
    /// </summary>
    public static bool IsAssignableTenantRole(TenantRole role) =>
        role is TenantRole.Analyst or TenantRole.Manager or TenantRole.TenantAdmin;

    /// <summary>Aparo canônico do nome de exibição (o valor efetivamente persistido).</summary>
    public static string NormalizeDisplayName(string? raw) => (raw ?? "").Trim();

    /// <summary>Nome de exibição obrigatório e dentro do teto — a mesma regra na criação e na atualização.</summary>
    public static bool IsValidDisplayName(string? raw)
    {
        var name = NormalizeDisplayName(raw);
        return name.Length is > 0 and <= MaxDisplayNameLength;
    }
}
