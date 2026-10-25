# [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] Módulo de FIXTURE — substitui o MicrosoftTeams SÓ nos testes. Não fala com rede nenhuma.
# Expõe os mesmos nomes de comando que o script embutido do AEGIS usa, com respostas no formato documentado. O cenário é
# escolhido pelo token entregue a Connect-MicrosoftTeams (o script não aceita nada além de tokens):
#   cenario-sucesso     — todas as leituras respondem;
#   cenario-aviso       — a conexão escreve aviso e texto de host na saída padrão (fluxos misturados);
#   cenario-queda       — o processo morre (código 137) durante a leitura das políticas de reunião;
#   credencial-invalida — a conexão é recusada como o serviço recusa (AADSTS).
# Formatos de resposta: ZERO objetos (Get-CsGroupPolicyAssignment), UM (Get-CsTeamsClientConfiguration) e VÁRIOS
# (Get-CsTeamsMeetingPolicy: a global e uma marcada).
Set-StrictMode -Version Latest

class AllowAllKnownDomains { }

$script:Scenario = $null

function Connect-MicrosoftTeams {
    [CmdletBinding()]
    param([string[]]$AccessTokens)
    if ($null -eq $AccessTokens -or $AccessTokens.Count -ne 2) { throw 'fixture: esperados dois tokens (Graph e administração do Teams).' }
    $cenario = $AccessTokens[1]
    if ($cenario -eq 'credencial-invalida') { throw 'AADSTS7000215: Invalid client secret provided. (fixture)' }
    $script:Scenario = $cenario
    if ($cenario -eq 'cenario-aviso') {
        Write-Warning 'fixture: aviso do módulo durante a conexão.'
        Write-Host 'fixture: texto de host do módulo.'
    }
    [pscustomobject]@{ Account = 'aplicativo'; Environment = 'AzureCloud'; Tenant = 'fixture' }
}

function Disconnect-MicrosoftTeams {
    [CmdletBinding(SupportsShouldProcess)]
    param()
    $script:Scenario = $null
}

function Get-CsTeamsClientConfiguration {
    [pscustomobject]@{
        Identity = 'Global'; AllowEmailIntoChannel = $false; AllowDropBox = $false; AllowBox = $false; AllowGoogleDrive = $false
        AllowShareFile = $false; AllowEgnyte = $false; AllowGuestUser = $true
    }
}

function Get-CsTenantFederationConfiguration {
    [pscustomobject]@{
        AllowFederatedUsers = $true; AllowedDomains = [AllowAllKnownDomains]::new(); BlockedDomains = @(); BlockAllSubdomains = $false
        AllowTeamsConsumer = $false; AllowTeamsConsumerInbound = $false; ExternalAccessWithTrialTenants = 'Blocked'
        AllowedTrialTenantDomains = @(); RestrictTeamsConsumerToExternalUserProfiles = $true
    }
}

function Get-CsTeamsMeetingPolicy {
    if ($script:Scenario -eq 'cenario-queda') { [Environment]::Exit(137) }
    [pscustomobject]@{
        Identity = 'Global'; AllowAnonymousUsersToJoinMeeting = $false; AllowAnonymousUsersToStartMeeting = $false
        AutoAdmittedUsers = 'EveryoneInCompanyExcludingGuests'; AllowPSTNUsersToBypassLobby = $false; MeetingChatEnabledType = 'EnabledExceptAnonymous'
        DesignatedPresenterRoleMode = 'OrganizerOnlyUserOverride'; AllowExternalParticipantGiveRequestControl = $false
        AllowExternalNonTrustedMeetingChat = $false; AllowCloudRecording = $false
    }
    [pscustomobject]@{
        Identity = 'Tag:Restrita'; AllowAnonymousUsersToJoinMeeting = $false; AllowAnonymousUsersToStartMeeting = $false
        AutoAdmittedUsers = 'OrganizerOnly'; AllowPSTNUsersToBypassLobby = $false; MeetingChatEnabledType = 'Disabled'
        DesignatedPresenterRoleMode = 'OrganizerOnlyUserOverride'; AllowExternalParticipantGiveRequestControl = $false
        AllowExternalNonTrustedMeetingChat = $false; AllowCloudRecording = $false
    }
}

function Get-CsTeamsMessagingPolicy { [pscustomobject]@{ Identity = 'Global'; AllowSecurityEndUserReporting = $true } }

function Get-CsTeamsAppPermissionPolicy {
    [pscustomobject]@{
        Identity = 'Global'; DefaultCatalogAppsType = 'AllowedAppList'; DefaultCatalogApps = @(); GlobalCatalogAppsType = 'AllowedAppList'
        GlobalCatalogApps = @(); PrivateCatalogAppsType = 'AllowedAppList'; PrivateCatalogApps = @()
    }
}

function Get-CsGroupPolicyAssignment { @() }

Export-ModuleMember -Function Connect-MicrosoftTeams, Disconnect-MicrosoftTeams, Get-CsTeamsClientConfiguration,
    Get-CsTenantFederationConfiguration, Get-CsTeamsMeetingPolicy, Get-CsTeamsMessagingPolicy, Get-CsTeamsAppPermissionPolicy,
    Get-CsGroupPolicyAssignment
