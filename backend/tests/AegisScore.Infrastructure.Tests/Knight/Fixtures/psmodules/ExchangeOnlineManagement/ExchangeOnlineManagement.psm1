# [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] Módulo de FIXTURE — substitui o ExchangeOnlineManagement SÓ nos testes. Não fala com
# rede nenhuma. Expõe os comandos que os scripts embutidos do AEGIS usam (Exchange Online e o perfil Purview do adaptador de
# proteção), com respostas no formato documentado. O cenário é escolhido pelo token entregue a Connect-ExchangeOnline:
#   cenario-sucesso     — todas as leituras respondem;
#   cenario-aviso       — a conexão e uma leitura escrevem aviso e texto de host na saída padrão (fluxos misturados);
#   cenario-queda       — o processo morre (código 137) durante a leitura das políticas de compartilhamento;
#   credencial-invalida — a conexão é recusada como o serviço recusa (AADSTS).
# Formatos de resposta: há leituras com ZERO objetos (Get-TransportRule, Get-TeamsProtectionPolicy), com UM objeto
# (Get-OrganizationConfig, Get-AtpPolicyForO365, Get-EmailTenantSettings) e com VÁRIOS (Get-RoleAssignmentPolicy, Get-AcceptedDomain,
# Get-DkimSigningConfig) — o PowerShell trata os três de forma diferente no pipeline e o script precisa entregar sempre uma lista.
# A conexão EXIGE -EXOModuleBasePath apontando para um diretório existente: prova que o adaptador entrega o diretório privado
# da execução. Connect-IPPSSession recusa ser chamado fora do Windows: prova que a limitação é detectada ANTES da sessão.
Set-StrictMode -Version Latest

$script:Scenario = $null

function Connect-ExchangeOnline {
    [CmdletBinding()]
    param(
        [string]$AccessToken, [string]$Organization, [string[]]$CommandName, [switch]$ShowBanner,
        [switch]$SkipLoadingFormatData, [string]$EXOModuleBasePath)
    if ($AccessToken -eq 'credencial-invalida') { throw 'AADSTS7000215: Invalid client secret provided. (fixture)' }
    if ([string]::IsNullOrWhiteSpace($Organization)) { throw 'fixture: -Organization é obrigatório com token de aplicativo.' }
    if ([string]::IsNullOrWhiteSpace($EXOModuleBasePath) -or -not (Test-Path -LiteralPath $EXOModuleBasePath)) {
        throw 'fixture: -EXOModuleBasePath ausente ou inexistente.'
    }
    $script:Scenario = $AccessToken
    if ($AccessToken -eq 'cenario-aviso') {
        Write-Warning 'fixture: aviso do módulo durante a conexão.'
        Write-Host 'fixture: texto de host do módulo.'
    }
}

function Connect-IPPSSession {
    [CmdletBinding()]
    param([string]$AccessToken, [string]$Organization, [string[]]$CommandName, [switch]$ShowBanner, [string]$EXOModuleBasePath)
    if (-not $IsWindows) { throw 'fixture: Connect-IPPSSession não pode ser chamado nesta plataforma (a documentação diz que não existe aqui).' }
    if ([string]::IsNullOrWhiteSpace($AccessToken)) { throw 'fixture: token ausente.' }
}

function Disconnect-ExchangeOnline {
    [CmdletBinding(SupportsShouldProcess)]
    param()
    $script:Scenario = $null
}

function Get-EXOMailbox { @() }

function Get-OrganizationConfig {
    if ($script:Scenario -eq 'cenario-aviso') { Write-Warning 'fixture: aviso de uma leitura.' }
    [pscustomobject]@{
        AuditDisabled = $false; CustomerLockBoxEnabled = $true; OAuth2ClientProfileEnabled = $true; MailTipsAllTipsEnabled = $true
        MailTipsExternalRecipientsTipsEnabled = $true; MailTipsGroupMetricsEnabled = $true; MailTipsLargeAudienceThreshold = 25
        BookingsEnabled = $false; RejectDirectSend = $true
    }
}

function Get-TransportConfig { [pscustomobject]@{ SmtpClientAuthenticationDisabled = $true } }

function Get-SharingPolicy {
    if ($script:Scenario -eq 'cenario-queda') { [Environment]::Exit(137) }
    [pscustomobject]@{ Identity = 'Default Sharing Policy'; Name = 'Default Sharing Policy'; Default = $true; Enabled = $true; Domains = @('Anonymous:CalendarSharingFreeBusySimple') }
}

function Get-OwaMailboxPolicy {
    [pscustomobject]@{
        Identity = 'OwaMailboxPolicy-Default'; Name = 'OwaMailboxPolicy-Default'; IsDefault = $true; AdditionalStorageProvidersAvailable = $false
        PersonalAccountsEnabled = $false; PersonalAccountCalendarsEnabled = $false; BookingsMailboxCreationEnabled = $false
    }
}

function Get-TransportRule { @() }

function Get-RoleAssignmentPolicy {
    [pscustomobject]@{ Identity = 'Default Role Assignment Policy'; Name = 'Default Role Assignment Policy'; IsDefault = $true; AssignedRoles = @('MyBaseOptions') }
    [pscustomobject]@{ Identity = 'Política restrita'; Name = 'Política restrita'; IsDefault = $false; AssignedRoles = @() }
}

function Get-ExternalInOutlook { [pscustomobject]@{ Identity = 'fixture'; Enabled = $true; AllowList = @() } }

function Get-HostedOutboundSpamFilterPolicy {
    [pscustomobject]@{ Identity = 'Default'; Name = 'Default'; IsDefault = $true; AutoForwardingMode = 'Off' }
}

function Get-Mailbox {
    [CmdletBinding()]
    param([object]$ResultSize)
    [pscustomobject]@{
        ExternalDirectoryObjectId = '00000000-0000-4000-8000-0000000000a1'; UserPrincipalName = 'ana@demo.example.com'; DisplayName = 'Ana Prado'
        RecipientTypeDetails = 'UserMailbox'; AuditEnabled = $true
        AuditOwner = @('ApplyRecord', 'HardDelete', 'MailItemsAccessed', 'MoveToDeletedItems', 'Send', 'SoftDelete', 'Update', 'UpdateCalendarDelegation', 'UpdateFolderPermissions', 'UpdateInboxRules')
        AuditDelegate = @('ApplyRecord', 'Create', 'HardDelete', 'MailItemsAccessed', 'Move', 'MoveToDeletedItems', 'SendAs', 'SendOnBehalf', 'SoftDelete', 'Update', 'UpdateFolderPermissions', 'UpdateInboxRules')
        AuditAdmin = @('ApplyRecord', 'Copy', 'Create', 'HardDelete', 'MailItemsAccessed', 'Move', 'MoveToDeletedItems', 'Send', 'SendAs', 'SendOnBehalf', 'SoftDelete', 'Update', 'UpdateCalendarDelegation', 'UpdateFolderPermissions', 'UpdateInboxRules')
        ForwardingSmtpAddress = $null; ForwardingAddress = $null; DeliverToMailboxAndForward = $false
        RoleAssignmentPolicy = 'Default Role Assignment Policy'; SharingPolicy = 'Default Sharing Policy'
    }
}

function Get-User {
    [CmdletBinding()]
    param([object]$ResultSize, [switch]$IsVIP)
    [pscustomobject]@{ ExternalDirectoryObjectId = '00000000-0000-4000-8000-0000000000a1'; UserPrincipalName = 'ana@demo.example.com'; AccountDisabled = $false }
}

function Get-CASMailbox {
    [CmdletBinding()]
    param([object]$ResultSize)
    [pscustomobject]@{
        ExternalDirectoryObjectId = '00000000-0000-4000-8000-0000000000a1'; UserPrincipalName = 'ana@demo.example.com'; DisplayName = 'Ana Prado'
        SmtpClientAuthenticationDisabled = $null; OwaMailboxPolicy = 'OwaMailboxPolicy-Default'
    }
}

function Get-MailboxAuditBypassAssociation {
    [CmdletBinding()]
    param([object]$ResultSize)
    [pscustomobject]@{ Identity = 'ana'; Name = 'ana'; AuditBypassEnabled = $false }
}

function Get-AdminAuditLogConfig { [pscustomobject]@{ UnifiedAuditLogIngestionEnabled = $true } }

function Get-DlpCompliancePolicy {
    [pscustomobject]@{
        Identity = 'dlp-1'; Name = 'Política de DLP'; Mode = 'Enable'; Enabled = $true; Workload = @('Exchange', 'SharePoint')
        ExchangeLocation = @('All'); SharePointLocation = @('All'); OneDriveLocation = @(); TeamsLocation = @(); EnforcementPlanes = @(); Locations = @()
    }
}

function Get-LabelPolicy {
    [pscustomobject]@{ Identity = 'rotulos-1'; Name = 'Rótulos'; Enabled = $true; Mode = 'Enforce'; Labels = @('Confidencial'); ExchangeLocation = @('All') }
}

# ---- Defender para Office 365 (perfil de proteção) ----

function Get-AtpPolicyForO365 { [pscustomobject]@{ EnableATPForSPOTeamsODB = $true; EnableSafeDocs = $true; AllowSafeDocsOpen = $false } }
function Get-SafeLinksRule { @() }
function Get-SafeLinksPolicy { [pscustomobject]@{ Identity = 'Built-In Protection Policy'; Name = 'Built-In Protection Policy'; EnableSafeLinksForEmail = $true } }
function Get-SafeAttachmentRule { @() }
function Get-SafeAttachmentPolicy { [pscustomobject]@{ Identity = 'Built-In Protection Policy'; Name = 'Built-In Protection Policy'; Enable = $true; Action = 'Block' } }
function Get-MalwareFilterRule { @() }
function Get-MalwareFilterPolicy { [pscustomobject]@{ Identity = 'Default'; Name = 'Default'; IsDefault = $true; EnableFileFilter = $true } }
function Get-HostedContentFilterRule { @() }
function Get-HostedContentFilterPolicy { [pscustomobject]@{ Identity = 'Default'; Name = 'Default'; IsDefault = $true; AllowedSenderDomains = @() } }
function Get-HostedOutboundSpamFilterRule { @() }
function Get-HostedConnectionFilterPolicy { [pscustomobject]@{ Identity = 'Default'; IPAllowList = @(); EnableSafeList = $false } }
function Get-AntiPhishRule { @() }
function Get-AntiPhishPolicy { [pscustomobject]@{ Identity = 'Office365 AntiPhish Default'; Name = 'Office365 AntiPhish Default'; IsDefault = $true; Enabled = $true } }
function Get-DkimSigningConfig {
    [pscustomobject]@{ Domain = 'demo.example.com'; Enabled = $true; Status = 'Valid' }
    [pscustomobject]@{ Domain = 'demo.onmicrosoft.com'; Enabled = $true; Status = 'Valid' }
}
function Get-AcceptedDomain {
    [pscustomobject]@{ DomainName = 'demo.example.com'; DomainType = 'Authoritative'; Default = $true }
    [pscustomobject]@{ DomainName = 'demo.onmicrosoft.com'; DomainType = 'Authoritative'; Default = $false }
}
function Get-TeamsProtectionPolicy { @() }
function Get-EmailTenantSettings { [pscustomobject]@{ EnablePriorityAccountProtection = $true } }
function Get-EOPProtectionPolicyRule { [pscustomobject]@{ Identity = 'Standard Preset Security Policy'; Name = 'Standard Preset Security Policy'; State = 'Enabled'; Priority = 0 } }
function Get-ATPProtectionPolicyRule { @() }
function Get-ReportSubmissionPolicy { [pscustomobject]@{ Identity = 'DefaultReportSubmissionPolicy'; ReportJunkToCustomizedAddress = $false } }

Export-ModuleMember -Function *
