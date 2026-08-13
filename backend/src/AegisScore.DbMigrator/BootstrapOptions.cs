using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace AegisScore.DbMigrator;

/// <summary>
/// [Homologação] Configuração do bootstrap do PRIMEIRO administrador (seção <c>Bootstrap</c>).
///
/// ⚠️ NENHUM valor pertence a <c>appsettings.json</c>. E-mail, senha e nome/identificador do tenant chegam
/// SOMENTE por variáveis de ambiente secretas (<c>Bootstrap__AdminEmail</c>, <c>Bootstrap__AdminPassword</c>,
/// <c>Bootstrap__TenantName</c>, <c>Bootstrap__TenantSlug</c>), <c>dotnet user-secrets</c> (dev) ou secret
/// manager — todos entram pelo mesmo <c>IConfiguration</c>, sem tocar o arquivo versionado.
///
/// A ativação é EXPLÍCITA (<c>Bootstrap__Enabled=true</c>): fora isso o migrator não cria nada. Depois do
/// primeiro deploy, defina <c>Bootstrap__Enabled=false</c> (ou remova as variáveis) para desligá-lo.
/// </summary>
public sealed class BootstrapOptions
{
    public const string SectionName = "Bootstrap";

    /// <summary>Piso de comprimento da senha — alinhado ao <c>IdentityProvisioningService</c> (NIST SP 800-63B).</summary>
    private const int MinPasswordLength = 12;

    /// <summary>Teto de comprimento da senha — barra entrada patológica antes do PBKDF2. Nada é truncado.</summary>
    private const int MaxPasswordLength = 128;

    /// <summary>Espelha o <c>HasMaxLength</c> da coluna de e-mail.</summary>
    private const int MaxEmailLength = 256;

    /// <summary>Formato conservador do e-mail JÁ normalizado — mesma regra do <c>IdentityProvisioningService</c>.</summary>
    private static readonly Regex EmailPattern = new(
        @"^[a-z0-9._%+-]+@[a-z0-9-]+(\.[a-z0-9-]+)+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Slug: minúsculas, dígitos e hífens internos, 2–64 caracteres — mesma regra do <c>TenantManagementService</c>.</summary>
    private static readonly Regex SlugPattern = new(
        "^[a-z0-9][a-z0-9-]{0,62}[a-z0-9]$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public bool Enabled { get; private init; }
    public string AdminEmail { get; private init; } = "";
    public string AdminPassword { get; private init; } = "";
    public string AdminDisplayName { get; private init; } = "";
    public string TenantName { get; private init; } = "";
    public string TenantSlug { get; private init; } = "";

    /// <summary>Mensagem de recusa quando <see cref="Enabled"/> é true mas a configuração é inválida.</summary>
    public string? Error { get; private init; }

    /// <summary>
    /// Lê e valida a seção <c>Bootstrap</c>. Quando desabilitado, devolve <see cref="Enabled"/>=false e nada
    /// mais é validado. Quando habilitado, valida e-mail, senha, nome e slug (derivando o slug do nome se
    /// ausente); qualquer recusa vem em <see cref="Error"/> — a senha NUNCA aparece na mensagem.
    /// </summary>
    public static BootstrapOptions Load(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);

        if (!section.GetValue("Enabled", false))
            return new BootstrapOptions { Enabled = false };

        var email = Normalize(section["AdminEmail"]);
        if (email.Length is 0 or > MaxEmailLength || !EmailPattern.IsMatch(email))
            return Invalid("Bootstrap:AdminEmail ausente ou em formato inválido.");

        // A senha NÃO é trimada — espaço é caractere legítimo e aparar quebraria o login seguinte.
        var password = section["AdminPassword"] ?? "";
        if (password.Length < MinPasswordLength || password.Length > MaxPasswordLength)
            return Invalid(
                $"Bootstrap:AdminPassword deve ter entre {MinPasswordLength} e {MaxPasswordLength} caracteres. " +
                "Prefira uma frase longa (não exigimos maiúsculas, dígitos ou símbolos — NIST SP 800-63B).");
        if (string.IsNullOrWhiteSpace(password))
            return Invalid("Bootstrap:AdminPassword não pode ser composta apenas de espaços.");

        var tenantName = (section["TenantName"] ?? "").Trim();
        if (tenantName.Length == 0)
            return Invalid("Bootstrap:TenantName é obrigatório.");

        // Slug: usa o informado ou DERIVA do nome. É identificador público (URL/chave de onboarding).
        var rawSlug = section["TenantSlug"];
        var slug = string.IsNullOrWhiteSpace(rawSlug) ? DeriveSlug(tenantName) : Normalize(rawSlug);
        if (!SlugPattern.IsMatch(slug))
            return Invalid(
                "Bootstrap:TenantSlug inválido (ou não foi possível derivá-lo do nome). Use 2–64 caracteres: " +
                "minúsculas, dígitos e hífens internos (ex.: 'cliente-homolog').");

        var displayName = (section["AdminDisplayName"] ?? "").Trim();
        if (displayName.Length == 0)
            displayName = "Administrador";

        return new BootstrapOptions
        {
            Enabled = true,
            AdminEmail = email,
            AdminPassword = password,
            AdminDisplayName = displayName,
            TenantName = tenantName,
            TenantSlug = slug,
        };
    }

    private static BootstrapOptions Invalid(string error) => new() { Enabled = true, Error = error };

    /// <summary>Mesma normalização do login/provisionamento — senão o login não acha o que foi gravado.</summary>
    private static string Normalize(string? raw) => (raw ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// Deriva um slug do nome do tenant: minúsculas, espaços/símbolos viram hífen, colapsa repetições e apara
    /// hífens das pontas. Best-effort — se o resultado não bater o <see cref="SlugPattern"/>, a validação
    /// recusa e orienta o operador a informar <c>Bootstrap:TenantSlug</c> explicitamente.
    /// </summary>
    private static string DeriveSlug(string name)
    {
        var lowered = name.Trim().ToLowerInvariant();
        var mapped = Regex.Replace(lowered, "[^a-z0-9]+", "-");
        return mapped.Trim('-');
    }
}
