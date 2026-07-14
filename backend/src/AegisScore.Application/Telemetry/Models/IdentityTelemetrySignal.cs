namespace AegisScore.Application.Telemetry.Models;

/// <summary>
/// Retrato de POSTURA DE IDENTIDADE de um tenant, normalizado na borda a partir da telemetria do
/// Microsoft Entra ID (inspirado nos Indicadores de Exposição de frameworks de assessment de identidade
/// como o Purple Knight). É o análogo tipado do <c>CategoryTelemetrySignal</c> — mas, diferente daquele
/// (um sinal por controle), a postura de identidade é MULTI-CONTROLE: o mesmo retrato alimenta tanto
/// <c>PR.AA-01</c> (a dimensão MFA/menor privilégio) quanto <c>GV.RR-01</c> (a dimensão governança de
/// identidade / excesso de administradores). Por isso não carrega um único <c>SubcategoryCode</c>: o alvo
/// NIST é atribuído na ingestão (uma vez por controle), não no sinal.
///
/// Além das métricas de exposição, transporta o CONTEXTO DE REDE (controles compensatórios) — que o Entra
/// não conhece, pois é conhecimento de infraestrutura (microssegmentação/firewall). É o que permite ao
/// motor PONDERAR falsos positivos de ambientes industriais (OT/IoT): uma "urdideira" ou terminal fabril
/// sem MFA por limitação de legado, MAS isolado na rede, é risco compensado — não falha crítica cega.
/// </summary>
/// <param name="TotalPrivilegedAccounts">Total de contas privilegiadas (Global Admins + demais papéis privilegiados). Indicador PK: "More than 10 Privileged Administrators exist".</param>
/// <param name="PrivilegedAccountsWithoutMfa">Contas privilegiadas SEM MFA efetivo. Indicador PK: "MFA not configured for privileged accounts" — falha crítica de PR.AA (salvo controle compensatório).</param>
/// <param name="PrivilegedAccountsWithMailbox">Contas privilegiadas COM caixa de correio ativa (superfície de phishing sobre o admin). Indicador PK: "Privileged accounts with mailbox".</param>
/// <param name="InactiveGuestAccountsOver30Days">Contas de convidados (Guests) inativas há mais de 30 dias — acesso de terceiros esquecido. Indicador PK: "Guest accounts that were inactive for more than 30 days".</param>
/// <param name="MfaExemptServiceAccounts">Contas de serviço/OT (ex.: terminais fabris — urdideira, costura) que NÃO suportam MFA por natureza técnica. Base do controle compensatório: sua presença explica o "sem MFA".</param>
/// <param name="HasNetworkIsolation">Controle compensatório: os ativos sem MFA estão em rede isolada/segmentada (VLAN OT, air gap). Informado pela infraestrutura, não pelo Entra. Reduz o risco de privilégio sem MFA de falha crítica para mitigado.</param>
/// <param name="CompensatingControls">Outras salvaguardas compensatórias declaradas (ex.: "PAM", "Jump Host", "Conditional Access por localização"). Contexto/evidência para o motor; extensível.</param>
public record IdentityTelemetrySignal(
    int TotalPrivilegedAccounts,
    int PrivilegedAccountsWithoutMfa,
    int PrivilegedAccountsWithMailbox,
    int InactiveGuestAccountsOver30Days,
    IReadOnlyList<string> MfaExemptServiceAccounts,
    bool HasNetworkIsolation = false,
    IReadOnlyList<string>? CompensatingControls = null)
{
    /// <summary>
    /// Projeta o retrato tipado nas linhas de métrica CANÔNICAS que o motor lê — os MESMOS rótulos que o
    /// <c>StubLlmClient</c> parseia por regex e que o prompt do motor real recebe (contrato de fio, não
    /// apresentação). Centraliza aqui os rótulos para que a ingestão (Api) e a heurística (StubLlmClient)
    /// concordem por construção; espelha o que o <c>TelemetryController</c> faz inline para as demais
    /// categorias. O mesmo conjunto de linhas alimenta PR.AA-01 e GV.RR-01 — cada regra extrai a sua métrica.
    /// A linha de isolamento de rede é o gatilho do controle compensatório na regra de PR.AA.
    /// </summary>
    public IReadOnlyList<string> ToMetricLines()
    {
        var lines = new List<string>
        {
            $"Total Privileged Accounts: {TotalPrivilegedAccounts}",
            $"Privileged Accounts Without MFA: {PrivilegedAccountsWithoutMfa}",
            $"Privileged Accounts With Mailbox: {PrivilegedAccountsWithMailbox}",
            $"Inactive Guest Accounts Over 30 Days: {InactiveGuestAccountsOver30Days}",
            $"MFA-Exempt Service Accounts: {MfaExemptServiceAccounts.Count}"
                + (MfaExemptServiceAccounts.Count > 0 ? $" ({string.Join(", ", MfaExemptServiceAccounts)})" : ""),
            $"Compensating Control: Network Isolation = {(HasNetworkIsolation ? "True" : "False")}",
        };
        if (CompensatingControls is { Count: > 0 })
            lines.Add($"Compensating Controls: {string.Join(", ", CompensatingControls)}");
        return lines;
    }
}
