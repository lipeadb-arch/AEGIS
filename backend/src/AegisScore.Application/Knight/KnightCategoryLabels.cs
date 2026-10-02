using AegisScore.Domain;

namespace AegisScore.Application.Knight;

/// <summary>
/// [AEGIS-KNIGHT-COVERAGE-04] Rótulo em português de cada categoria de controle — autoridade única dos relatórios. Um
/// valor sem rótulo aqui é defeito de catálogo (o teste de cobertura exige rótulo para todos): o nome interno do
/// enumerado nunca chega ao leitor.
/// </summary>
public static class KnightCategoryLabels
{
    public static string Label(KnightIndicatorCategory category) => category switch
    {
        KnightIndicatorCategory.PrivilegedAccess => "Acesso privilegiado",
        KnightIndicatorCategory.IdentityGovernance => "Governança de identidade",
        KnightIndicatorCategory.AccountHygiene => "Higiene de contas",
        KnightIndicatorCategory.GuestAccess => "Acesso de convidados",
        KnightIndicatorCategory.ServiceAccounts => "Contas de serviço",
        KnightIndicatorCategory.TenantConfiguration => "Configuração do locatário",
        KnightIndicatorCategory.AuthenticationPolicy => "Política de autenticação",
        KnightIndicatorCategory.ApplicationGovernance => "Governança de aplicações",
        KnightIndicatorCategory.DeviceGovernance => "Governança de dispositivos",
        KnightIndicatorCategory.CollaborationSecurity => "Colaboração e comunicação",
        KnightIndicatorCategory.ThreatProtection => "Proteção contra ameaças",
        KnightIndicatorCategory.DataProtection => "Proteção de dados",
        KnightIndicatorCategory.CloudInfrastructure => "Infraestrutura em nuvem",
        _ => "Outra categoria",
    };
}
