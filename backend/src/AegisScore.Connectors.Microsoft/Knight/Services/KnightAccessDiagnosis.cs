using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AegisScore.Application.Knight;
using AegisScore.Connectors.Microsoft.Knight.Teams;

namespace AegisScore.Connectors.Microsoft.Knight.Services;

// ============================================================================
//  [AEGIS-KNIGHT-TEAMS-FABRIC-ACCESS-01] Diagnóstico de acesso do Teams e do Fabric, montado só com evidência
// ============================================================================
// Usado pela sincronização (motivo das capacidades recusadas, persistido e apresentado no relatório) e pelo "Testar
// conexão" (linha da fonte). Três blocos, sempre separados: o que foi VERIFICADO, o que NÃO É VERIFICÁVEL com esta
// credencial, e a AÇÃO — só quando a evidência a demonstra. Nenhum bloco nasce do código da recusa.

/// <summary>O diagnóstico de UMA fonte: fatos verificados, pontos não verificáveis e ações demonstradas.</summary>
/// <param name="Notes">Fatos DOCUMENTAIS que evitam uma leitura errada da recusa (não são observações desta execução).</param>
public sealed record KnightAccessDiagnosis(
    IReadOnlyList<string> Verified, IReadOnlyList<string> NotVerifiable, IReadOnlyList<string> Actions, IReadOnlyList<string>? Notes = null)
{
    public static readonly KnightAccessDiagnosis Empty = new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

    /// <summary>Só o bloco de não verificável — quando o próprio diagnóstico falhou.</summary>
    public static KnightAccessDiagnosis Unavailable { get; } =
        new(Array.Empty<string>(), new[] { "o diagnóstico de acesso não pôde ser concluído nesta execução" }, Array.Empty<string>());

    /// <summary>Texto em português para o motivo da capacidade e para a linha do teste de conexão.</summary>
    public string Text
    {
        get
        {
            var parts = new List<string>();
            if (Verified.Count > 0) parts.Add("Verificado: " + Join(Verified));
            if (NotVerifiable.Count > 0) parts.Add("Não verificável: " + Join(NotVerifiable));
            parts.Add(Actions.Count > 0
                ? "Ação necessária: " + Join(Actions)
                : "Nenhuma ação específica é demonstrada pela evidência disponível.");
            if (Notes is { Count: > 0 }) parts.Add("Observação: " + Join(Notes));
            return string.Join(" ", parts);
        }
    }

    private static string Join(IEnumerable<string> items) => string.Join("; ", items.Select(i => i.TrimEnd('.'))) + ".";
}

public static class KnightAccessDiagnostics
{
    internal const string OrganizationReadAll = "Organization.Read.All";
    internal const string DirectoryReadAll = "Directory.Read.All";

    // ---- Microsoft Teams ------------------------------------------------------------------------------

    /// <summary>
    /// Depois de uma recusa na administração do Teams (conexão ou comando), com os dois tokens já emitidos. O papel
    /// verificado é o da APLICAÇÃO — o do token e o da entidade de serviço —, nunca o de quem opera o AEGIS.
    /// </summary>
    public static async Task<KnightAccessDiagnosis> TeamsAsync(
        IMicrosoftGraphCredentials cfg, TeamsAdminCredentials tokens, IEntraGraphClient? graph, CancellationToken ct)
    {
        var verified = new List<string>();
        var unknown = new List<string>();
        var actions = new List<string>();

        var graphClaims = AccessTokenClaims.TryRead(tokens.GraphToken);
        var teamsClaims = AccessTokenClaims.TryRead(tokens.TeamsToken);
        Identity(graphClaims, cfg, MicrosoftTokenResource.Graph, verified, unknown, actions);
        Identity(teamsClaims, cfg, MicrosoftTokenResource.TeamsAdmin, verified, unknown, actions);

        // Papel de diretório: a documentação da autenticação de aplicativo diz que o papel atribuído à aplicação vem NO
        // token e é dele que a sessão do módulo tira a autorização.
        var tokenRoles = teamsClaims?.DirectoryRoleTemplateIds.Where(DirectoryRoleNames.IsTeamsRelevant).ToList();
        if (teamsClaims is null)
        {
            // Já declarado por Identity(): o token ilegível deixa identidade, papel e permissões sem verificação.
        }
        else if (tokenRoles!.Count == 0)
            verified.Add("o token da administração do Teams não declara nenhum papel de diretório do Teams nem de leitura global (claim wids) — "
                + "e é desse papel no token que o módulo tira a autorização da sessão, segundo a documentação.");
        else
        {
            verified.Add($"o token da administração do Teams declara o(s) papel(is) {Names(tokenRoles)}.");
            if (tokenRoles.Any(DirectoryRoleNames.TeamsPowerShellDocumented.Contains))
                verified.Add("a recusa não decorre da ausência de papel de diretório no token.");
            else if (tokenRoles.All(DirectoryRoleNames.TeamsReadOnly.Contains))
                unknown.Add("se o papel de leitura presente basta para os comandos Get-Cs*: a tabela oficial de papéis do Teams cita Leitor do Teams "
                    + "(e o Leitor Global, nos papéis do Microsoft 365) só para o centro de administração e associa ao módulo PowerShell "
                    + "o Administrador do Teams, o de Comunicações e o de Telefonia; a recusa com esse papel é compatível com essa limitação, mas não a comprova.");
        }

        // Permissões configuradas para o recurso do Teams — a documentação diz que não são necessárias e podem causar falhas.
        if (teamsClaims is { Roles.Count: > 0 })
        {
            verified.Add($"o token da administração do Teams traz permissões de API configuradas para esse recurso ({string.Join(", ", teamsClaims.Roles)}).");
            actions.Add("remover da aplicação as permissões configuradas para «Skype and Teams Tenant Admin API»: a documentação da autenticação de "
                + "aplicativo do Teams diz que nenhuma é necessária e que configurá-las pode causar falhas.");
        }

        // Atribuições ativas da entidade de serviço (Graph, leitura completa).
        var assigned = graph is null ? null : await AssignmentsAsync(graph, tokens.GraphToken, cfg, verified, unknown, ct);
        if (teamsClaims is not null && tokenRoles!.Count == 0)
        {
            if (assigned is { State: AccessLookupState.Found } a && a.Value!.Any(r => DirectoryRoleNames.IsTeamsRelevant(r.RoleTemplateId)))
                actions.Add("a atribuição existe, mas não chegou ao token desta execução (atribuição recente ou restrita a uma unidade administrativa): "
                    + "sincronizar novamente depois da propagação e, persistindo, conferir se o escopo da atribuição é o locatário inteiro.");
            else
                actions.Add("atribuir um papel de diretório do Teams à entidade de serviço da aplicação (não à conta do operador). O de menor privilégio "
                    + "na tabela de papéis do Teams é Leitor do Teams; como a Microsoft não o associa explicitamente ao módulo PowerShell, "
                    + "a eficácia dele para os comandos Get-Cs* só se confirma numa nova sincronização.");
        }

        // Organization.Read.All — exigida pela documentação do módulo para TODOS os comandos.
        if (graphClaims is null)
        {
            // Já declarado por Identity() (inclui as permissões do token, Organization.Read.All entre elas).
        }
        else if (graphClaims.Roles.Contains(OrganizationReadAll, StringComparer.OrdinalIgnoreCase))
            verified.Add($"{OrganizationReadAll} (exigida pela documentação do módulo para todos os comandos) consta no token do Microsoft Graph.");
        else
        {
            verified.Add($"{OrganizationReadAll}, exigida pela documentação do módulo para todos os comandos, não consta no token do Microsoft Graph.");
            var directory = graphClaims.Roles.Contains(DirectoryReadAll, StringComparer.OrdinalIgnoreCase);
            if (directory)
                unknown.Add($"se o módulo aceita {DirectoryReadAll} no lugar de {OrganizationReadAll}: {DirectoryReadAll} consta no token e autoriza a "
                    + "leitura de /organization no Microsoft Graph, mas não é a permissão que a documentação do módulo indica.");
            else
                actions.Add($"conceder {OrganizationReadAll} (aplicativo, Microsoft Graph) à aplicação, com consentimento do administrador.");
            if (graph is not null)
            {
                var org = await new ServicePrincipalAccessReader(graph).OrganizationReadAsync(tokens.GraphToken, cfg, ct);
                if (org.State == AccessLookupState.NotVerifiable) unknown.Add($"a leitura efetiva de /organization com o token do Graph ({org.Failure}).");
                else verified.Add(org.Value ? "a leitura efetiva de /organization com o token do Graph foi aceita."
                    : $"a leitura efetiva de /organization com o token do Graph foi recusada ({org.Failure}).");
            }
        }

        return new KnightAccessDiagnosis(verified, unknown, actions);
    }

    // ---- Microsoft Fabric ------------------------------------------------------------------------------

    /// <summary>
    /// Depois de uma recusa em <c>/v1/admin/tenantsettings</c>. A configuração do portal NÃO é legível por esta credencial
    /// (é justamente a leitura recusada): o AEGIS nunca afirma que ela esteja desligada.
    /// </summary>
    public static async Task<KnightAccessDiagnosis> FabricAsync(
        IMicrosoftGraphCredentials cfg, string fabricToken, IEntraGraphClient? graph, CancellationToken ct)
    {
        var verified = new List<string>();
        var unknown = new List<string>();
        var actions = new List<string>();

        var claims = AccessTokenClaims.TryRead(fabricToken);
        Identity(claims, cfg, MicrosoftTokenResource.Fabric, verified, unknown, actions);

        if (claims is null)
        {
            // Já declarado por Identity(): inclui as permissões de aplicativo do Power BI/Fabric no token.
        }
        else if (claims.Roles.Count > 0)
        {
            verified.Add($"o token do Fabric traz permissões de aplicativo do serviço Power BI/Fabric com consentimento ({string.Join(", ", claims.Roles)}).");
            actions.Add("remover essas permissões de aplicativo do Power BI/Fabric da aplicação: a documentação exige que a aplicação usada como "
                + "entidade de serviço nas APIs de administração não tenha nenhuma permissão desse tipo que exija consentimento do administrador.");
        }
        else
            verified.Add("o token do Fabric não traz permissões de aplicativo do Power BI/Fabric, como a documentação exige para entidade de serviço.");

        if (graph is not null)
        {
            string? graphToken = null;
            try
            {
                graphToken = await graph.AcquireTokenAsync(cfg, ct);
            }
            catch (Exception ex) when (!KnightFailureClassifier.IsCallerCancellation(ex, ct))
            {
                unknown.Add($"a entidade de serviço no Microsoft Graph (token do Graph não emitido: {ServicePrincipalAccessReader.Failure(ex)}).");
            }
            if (graphToken is not null)
            {
                var reader = new ServicePrincipalAccessReader(graph);
                var sp = await reader.FindAsync(graphToken, cfg, ct);
                if (sp.State != AccessLookupState.Found)
                    unknown.Add(sp.State == AccessLookupState.ProvenAbsent
                        ? "a entidade de serviço da aplicação não foi encontrada no Microsoft Graph deste locatário."
                        : $"a entidade de serviço da aplicação no Microsoft Graph ({sp.Failure}).");
                else
                {
                    var groups = await reader.SecurityGroupCountAsync(graphToken, cfg, sp.Value!, ct);
                    switch (groups.State)
                    {
                        case AccessLookupState.ProvenAbsent:
                            verified.Add("a entidade de serviço da aplicação não pertence a nenhum grupo de segurança (leitura completa, direta e transitiva).");
                            actions.Add("incluir a entidade de serviço da aplicação num grupo de segurança e autorizar esse grupo na configuração do Fabric "
                                + "«Entidades de serviço podem acessar APIs de administração somente leitura» (ação de um administrador do Fabric).");
                            break;
                        case AccessLookupState.Found:
                            verified.Add($"a entidade de serviço da aplicação pertence a {groups.Value} grupo(s) de segurança.");
                            unknown.Add("se algum desses grupos está autorizado na configuração «Entidades de serviço podem acessar APIs de administração "
                                + "somente leitura» e se ela está habilitada: essa configuração só é legível pela leitura que foi recusada.");
                            break;
                        default:
                            unknown.Add($"os grupos de segurança da entidade de serviço ({groups.Failure}).");
                            break;
                    }
                }
            }
        }
        if (!unknown.Any(u => u.Contains("está habilitada", StringComparison.Ordinal)))
            unknown.Add("o estado da configuração «Entidades de serviço podem acessar APIs de administração somente leitura» — o AEGIS não a "
                + "consulta por outro caminho e não afirma que esteja desligada.");
        var notes = new[] { "os escopos delegados Tenant.Read.All/Tenant.ReadWrite.All valem para chamadas com usuário e não se aplicam a esta autenticação de aplicativo" };

        return new KnightAccessDiagnosis(verified, unknown, actions, notes);
    }

    // ---- Partes comuns ---------------------------------------------------------------------------------

    private static void Identity(AccessTokenClaims? claims, IMicrosoftGraphCredentials cfg, MicrosoftTokenResource resource,
        List<string> verified, List<string> unknown, List<string> actions)
    {
        if (claims is null)
        {
            unknown.Add($"o conteúdo do token de {resource.Name} (identidade, recurso, papéis e permissões declarados), que não pôde ser lido");
            return;
        }
        var mismatches = TokenIdentityCheck.Mismatches(claims, cfg, resource);
        if (mismatches.Count > 0)
        {
            verified.Add($"token de {resource.Name} incompatível: {string.Join("; ", mismatches)}.");
            actions.Add($"conferir o locatário e a aplicação configurados no conector: o token de {resource.Name} não corresponde a eles.");
            return;
        }
        verified.Add(claims.Audience is { } a && resource.Matches(a)
            ? $"token de {resource.Name} emitido para a aplicação e o locatário configurados, para o recurso esperado."
            : $"token de {resource.Name} emitido para a aplicação e o locatário configurados (recurso declarado: {claims.Audience ?? "não informado"}).");
    }

    private static async Task<AccessLookup<IReadOnlyList<DirectoryRoleAssignment>>?> AssignmentsAsync(
        IEntraGraphClient graph, string graphToken, IMicrosoftGraphCredentials cfg, List<string> verified, List<string> unknown, CancellationToken ct)
    {
        var reader = new ServicePrincipalAccessReader(graph);
        var sp = await reader.FindAsync(graphToken, cfg, ct);
        if (sp.State != AccessLookupState.Found)
        {
            unknown.Add(sp.State == AccessLookupState.ProvenAbsent
                ? "as atribuições de papel: a entidade de serviço da aplicação não foi encontrada no Microsoft Graph deste locatário."
                : $"as atribuições de papel da entidade de serviço ({sp.Failure}); nada se conclui sobre elas.");
            return AccessLookup<IReadOnlyList<DirectoryRoleAssignment>>.NotVerifiable(sp.Failure ?? "entidade de serviço não encontrada");
        }

        var roles = await reader.DirectoryRolesAsync(graphToken, cfg, sp.Value!, ct);
        switch (roles.State)
        {
            case AccessLookupState.ProvenAbsent:
                verified.Add("a entidade de serviço da aplicação não tem nenhuma atribuição ativa de papel de diretório (leitura completa).");
                break;
            case AccessLookupState.Found:
                var relevant = roles.Value!.Where(r => DirectoryRoleNames.IsTeamsRelevant(r.RoleTemplateId)).ToList();
                verified.Add(relevant.Count == 0
                    ? $"a entidade de serviço tem {roles.Value!.Count} atribuição(ões) ativa(s) de papel de diretório, nenhuma do Teams nem de leitura global."
                    : "atribuição(ões) ativa(s) da entidade de serviço: "
                      + string.Join(", ", relevant.Select(r => r.RoleName + (r.TenantWide ? " (locatário inteiro)" : " (escopo restrito)"))) + ".");
                break;
            default:
                unknown.Add($"as atribuições ativas de papel da entidade de serviço ({roles.Failure}); nada se conclui sobre elas.");
                break;
        }
        return roles;
    }

    private static string Names(IEnumerable<string> templateIds) =>
        string.Join(", ", templateIds.Select(id => DirectoryRoleNames.Name(id) ?? id));
}
