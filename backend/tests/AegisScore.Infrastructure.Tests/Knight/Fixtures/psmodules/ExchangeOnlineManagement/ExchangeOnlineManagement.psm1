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
# [AEGIS-KNIGHT-MEMORY-BUDGET] Com AEGIS_FIXTURE_OFFICIAL_MODULE_ROOT (só na reprodução combinada do CI), o módulo OFICIAL da imagem é
# importado aqui dentro antes dos substitutos: o percurso com fixture paga o peso real da importação. Nenhum comando oficial é chamado.
if ($env:AEGIS_FIXTURE_OFFICIAL_MODULE_ROOT) {
    $official = Get-ChildItem -LiteralPath (Join-Path $env:AEGIS_FIXTURE_OFFICIAL_MODULE_ROOT 'ExchangeOnlineManagement') -Filter 'ExchangeOnlineManagement.psd1' -Recurse | Select-Object -First 1
    Import-Module $official.FullName -ErrorAction Stop
}

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

# ---- Enumerações de caixas e contas ----
#
# [AEGIS-KNIGHT-RUNTIME-DIAGNOSIS] Get-EXOMailbox devolve o conjunto Minimum MAIS o que foi pedido em -Properties — e nada além, como
# a documentação do cmdlet descreve. Uma propriedade não pedida NÃO existe no objeto: se o script deixar de pedir alguma, a
# projeção dele precisa perceber. Get-Mailbox não existe neste módulo de fixture: voltar a chamá-lo falha como comando ausente.
# Cenários de enumeração (escolhidos pelo token):
#   cenario-volume              — 6000 caixas, contas e configurações de acesso: a leitura atinge o teto e chega TRUNCADA;
#   cenario-queda-tardia        — o processo morre (código 137) na leitura das contas, DEPOIS da enumeração das caixas;
#   cenario-projecao-incompleta — Get-EXOMailbox omite ForwardingSmtpAddress mesmo quando pedido (contrato rompido).

$script:MinimumMailboxProperties = @(
    'Alias', 'DisplayName', 'DistinguishedName', 'EmailAddresses', 'ExchangeVersion', 'ExternalDirectoryObjectId', 'Guid', 'Id', 'Name',
    'OrganizationId', 'PrimarySmtpAddress', 'RecipientType', 'RecipientTypeDetails', 'UserPrincipalName')

$script:OwnerActions = @('ApplyRecord', 'Create', 'HardDelete', 'MailItemsAccessed', 'Move', 'MoveToDeletedItems', 'Send', 'SoftDelete', 'Update', 'UpdateCalendarDelegation', 'UpdateFolderPermissions', 'UpdateInboxRules')
$script:DelegateActions = @('ApplyRecord', 'Create', 'HardDelete', 'MailItemsAccessed', 'Move', 'MoveToDeletedItems', 'SendAs', 'SendOnBehalf', 'SoftDelete', 'Update', 'UpdateFolderPermissions', 'UpdateInboxRules')
$script:AdminActions = @('ApplyRecord', 'Copy', 'Create', 'HardDelete', 'MailItemsAccessed', 'Move', 'MoveToDeletedItems', 'Send', 'SendAs', 'SendOnBehalf', 'SoftDelete', 'Update', 'UpdateCalendarDelegation', 'UpdateFolderPermissions', 'UpdateInboxRules')

function New-FixtureMailbox {
    param([string]$Id, [string]$Upn, [string]$Name, [string]$Type, [object[]]$Owner, $ForwardingSmtp, $DeliverAndForward, $Sharing)
    [ordered]@{
        Alias = $Upn.Split('@')[0]; DisplayName = $Name; DistinguishedName = "CN=$Name"; EmailAddresses = @("SMTP:$Upn"); ExchangeVersion = '0.20 (15.0.0.0)'
        ExternalDirectoryObjectId = $Id; Guid = $Id; Id = $Name; Name = $Name; OrganizationId = 'demo.onmicrosoft.com'; PrimarySmtpAddress = $Upn
        RecipientType = 'UserMailbox'; RecipientTypeDetails = $Type; UserPrincipalName = $Upn
        AuditEnabled = $true; AuditOwner = $Owner; AuditDelegate = $script:DelegateActions; AuditAdmin = $script:AdminActions; AuditLogAgeLimit = '90.00:00:00'
        ForwardingSmtpAddress = $ForwardingSmtp; ForwardingAddress = $null; DeliverToMailboxAndForward = $DeliverAndForward
        RoleAssignmentPolicy = 'Default Role Assignment Policy'; SharingPolicy = $Sharing; RetentionPolicy = 'Default MRM Policy'
        LitigationHoldEnabled = $false; ArchiveStatus = 'None'; CustomAttribute1 = ''
    }
}

function Get-FixtureMailboxes {
    # Emite em FLUXO, uma caixa por vez (como uma enumeração paginada): o módulo de fixture não segura a coleção inteira, para que a
    # memória medida no processo seja a do script, não a do substituto do fornecedor.
    param([int]$Limit)
    if ($script:Scenario -eq 'cenario-volume') {
        for ($i = 1; $i -le [Math]::Min(6000, $Limit); $i++) {
            $id = '00000000-0000-4000-9000-{0:d12}' -f $i
            New-FixtureMailbox $id "caixa$i@demo.example.com" "Caixa sintética $i" 'UserMailbox' $script:OwnerActions $null $false 'Default Sharing Policy'
        }
        return
    }
    # Distinções que a projeção precisa preservar: lista de auditoria COMPLETA × lista VAZIA; encaminhamento nulo × configurado;
    # entrega-e-encaminha falso × verdadeiro; política declarada × nula.
    $fixas = @(
        (New-FixtureMailbox '00000000-0000-4000-8000-0000000000a1' 'ana@demo.example.com' 'Ana Prado' 'UserMailbox' $script:OwnerActions $null $false 'Default Sharing Policy'),
        (New-FixtureMailbox '00000000-0000-4000-8000-0000000000a2' 'financeiro@demo.example.com' 'Financeiro' 'SharedMailbox' @() 'smtp:arquivo@example.net' $true $null),
        (New-FixtureMailbox '00000000-0000-4000-8000-0000000000a3' 'sala@demo.example.com' 'Sala de reunião' 'RoomMailbox' $script:OwnerActions $null $false 'Default Sharing Policy'))
    $fixas | Select-Object -First $Limit
}

function Get-EXOMailbox {
    [CmdletBinding()]
    param([string[]]$Properties, [string[]]$PropertySets, [object]$ResultSize = 1000)
    $keep = @($script:MinimumMailboxProperties) + @($Properties | Where-Object { $_ })
    if ($script:Scenario -eq 'cenario-projecao-incompleta') { $keep = @($keep | Where-Object { $_ -ne 'ForwardingSmtpAddress' }) }
    Get-FixtureMailboxes ([int]$ResultSize) | ForEach-Object {
        $m = $_
        $projected = [ordered]@{}
        foreach ($k in $m.Keys) { if ($keep -contains $k) { $projected[$k] = $m[$k] } }
        [pscustomobject]$projected
    }
}

function Get-User {
    [CmdletBinding()]
    param([object]$ResultSize, [switch]$IsVIP)
    if ($script:Scenario -eq 'cenario-queda-tardia') { [Environment]::Exit(137) }
    if ($script:Scenario -eq 'cenario-volume') {
        $limit = [int]$ResultSize
        for ($i = 1; $i -le [Math]::Min(6000, $limit); $i++) {
            [pscustomobject]@{ ExternalDirectoryObjectId = ('00000000-0000-4000-9000-{0:d12}' -f $i); UserPrincipalName = "caixa$i@demo.example.com"; AccountDisabled = $false }
        }
        return
    }
    [pscustomobject]@{ ExternalDirectoryObjectId = '00000000-0000-4000-8000-0000000000a1'; UserPrincipalName = 'ana@demo.example.com'; AccountDisabled = $false }
    [pscustomobject]@{ ExternalDirectoryObjectId = '00000000-0000-4000-8000-0000000000a2'; UserPrincipalName = 'financeiro@demo.example.com'; AccountDisabled = $true }
}

function Get-CASMailbox {
    [CmdletBinding()]
    param([object]$ResultSize)
    if ($script:Scenario -eq 'cenario-volume') {
        $limit = [int]$ResultSize
        for ($i = 1; $i -le [Math]::Min(6000, $limit); $i++) {
            [pscustomobject]@{
                ExternalDirectoryObjectId = ('00000000-0000-4000-9000-{0:d12}' -f $i); UserPrincipalName = "caixa$i@demo.example.com"; DisplayName = "Caixa sintética $i"
                SmtpClientAuthenticationDisabled = $null; OwaMailboxPolicy = 'OwaMailboxPolicy-Default'
            }
        }
        return
    }
    # Os TRÊS estados de SMTP AUTH por caixa: ausente/nulo = herda a organização; falso = HABILITA na caixa; verdadeiro = desabilita.
    [pscustomobject]@{
        ExternalDirectoryObjectId = '00000000-0000-4000-8000-0000000000a1'; UserPrincipalName = 'ana@demo.example.com'; DisplayName = 'Ana Prado'
        SmtpClientAuthenticationDisabled = $null; OwaMailboxPolicy = 'OwaMailboxPolicy-Default'
    }
    [pscustomobject]@{
        ExternalDirectoryObjectId = '00000000-0000-4000-8000-0000000000a2'; UserPrincipalName = 'financeiro@demo.example.com'; DisplayName = 'Financeiro'
        SmtpClientAuthenticationDisabled = $false; OwaMailboxPolicy = 'OwaMailboxPolicy-Default'
    }
    [pscustomobject]@{
        ExternalDirectoryObjectId = '00000000-0000-4000-8000-0000000000a3'; UserPrincipalName = 'sala@demo.example.com'; DisplayName = 'Sala de reunião'
        SmtpClientAuthenticationDisabled = $true; OwaMailboxPolicy = $null
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
