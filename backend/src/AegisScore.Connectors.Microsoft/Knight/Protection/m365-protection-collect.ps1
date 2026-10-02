#Requires -Version 7.2
<#
    [AEGIS-KNIGHT-COVERAGE-04] Adaptador de COLETA de proteção do Microsoft 365 — somente leitura.

    Dois perfis, os dois pelo módulo oficial ExchangeOnlineManagement:
      • defender — políticas do Microsoft Defender para Office 365, na MESMA sessão de aplicativo do Exchange Online
        (os comandos de leitura das políticas de proteção existem nessa sessão; nenhuma permissão nova);
      • purview  — a configuração do log de auditoria (sessão do Exchange Online) e as políticas de DLP e de rótulos
        (sessão do Security & Compliance, `Connect-IPPSSession -AccessToken`, documentado a partir da versão 3.8.0).

    Mesmas regras do adaptador do Exchange (exchange-collect.ps1), e pelo mesmo motivo:
      • é um RECURSO EMBUTIDO; o cliente não fornece script, comando, parâmetro nem destino;
      • só verbos Get;
      • o token chega por UMA linha JSON na entrada padrão — nunca por argumento nem variável de ambiente;
      • cada leitura registra o próprio desfecho; a falha de uma não interrompe as outras;
      • o texto bruto da exceção NÃO atravessa esta fronteira: só `errorCategory` e `errorId` (conjunto fixo de
        caracteres). A mensagem que o cliente lê é montada pelo AEGIS.

    ALCANCE DAS POLÍTICAS — uma política de proteção só age por uma REGRA habilitada (ou por ser a padrão). A leitura
    junta cada política à regra que a aplica, para o AEGIS não aprovar uma política que não alcança ninguém.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$script:DefenderCommands = @(
    'Get-AtpPolicyForO365',
    'Get-SafeLinksPolicy', 'Get-SafeLinksRule',
    'Get-SafeAttachmentPolicy', 'Get-SafeAttachmentRule',
    'Get-MalwareFilterPolicy', 'Get-MalwareFilterRule',
    'Get-HostedContentFilterPolicy', 'Get-HostedContentFilterRule',
    'Get-HostedOutboundSpamFilterPolicy', 'Get-HostedOutboundSpamFilterRule',
    'Get-HostedConnectionFilterPolicy',
    'Get-AntiPhishPolicy', 'Get-AntiPhishRule',
    'Get-DkimSigningConfig',
    'Get-AcceptedDomain',
    'Get-TeamsProtectionPolicy',
    'Get-EmailTenantSettings',
    'Get-User',
    'Get-EOPProtectionPolicyRule', 'Get-ATPProtectionPolicyRule'
)

$script:PurviewExchangeCommands = @('Get-AdminAuditLogConfig')
$script:PurviewComplianceCommands = @('Get-DlpCompliancePolicy', 'Get-LabelPolicy')

# ---- Utilitários (idênticos aos do adaptador do Exchange) -------------------------------------------------

function Get-Prop {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $p = $Object.PSObject.Properties[$Name]
    if ($null -eq $p) { return $null }
    return $p.Value
}

function Get-Bool {
    param($Object, [string]$Name)
    $v = Get-Prop $Object $Name
    if ($null -eq $v) { return $null }
    if ($v -is [bool]) { return $v }
    $s = ([string]$v).Trim()
    if ($s -eq '') { return $null }
    if ($s -in @('True', 'true', '1')) { return $true }
    if ($s -in @('False', 'false', '0')) { return $false }
    return $null
}

function Get-Text {
    param($Object, [string]$Name)
    $v = Get-Prop $Object $Name
    if ($null -eq $v) { return $null }
    $s = ([string]$v).Trim()
    if ($s -eq '') { return $null }
    return $s
}

function Get-Int {
    param($Object, [string]$Name)
    $v = Get-Prop $Object $Name
    if ($null -eq $v) { return $null }
    $n = 0
    if ([int]::TryParse(([string]$v).Trim(), [ref]$n)) { return $n }
    return $null
}

function Get-List {
    param($Object, [string]$Name)
    $v = Get-Prop $Object $Name
    if ($null -eq $v) { return @() }
    return @($v | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ -ne '' })
}

function Get-ErrorCategory {
    param([System.Management.Automation.ErrorRecord]$ErrorRecord)
    try {
        if ($null -eq $ErrorRecord) { return 'Error' }
        $ex = $ErrorRecord.Exception
        if ($null -eq $ex) { return 'Error' }
        if ($ex -is [System.Management.Automation.CommandNotFoundException]) { return 'Unavailable' }
        $m = [string]$ex.Message
        if ([string]::IsNullOrEmpty($m)) { return 'Error' }
        if ($m -match '(?i)429|throttl|too many requests|micro delay') { return 'Throttled' }
        if ($m -match '(?i)\b401\b|unauthorized|AADSTS|invalid.?token|expired') { return 'AuthenticationFailure' }
        if ($m -match '(?i)\b403\b|forbidden|access denied|not authorized|insufficient|privileg|role assigned') { return 'InsufficientPermission' }
        if ($m -match '(?i)\b50[0234]\b|service unavailable|timed out|timeout|temporarily') { return 'Unavailable' }
        if ($m -match '(?i)licen[sc]') { return 'LimitedByLicense' }
        return 'Error'
    }
    catch { return 'Error' }
}

function Get-ErrorId {
    param([System.Management.Automation.ErrorRecord]$ErrorRecord)
    try {
        if ($null -eq $ErrorRecord) { return $null }
        $parts = @()
        $fqid = [string]$ErrorRecord.FullyQualifiedErrorId
        if (-not [string]::IsNullOrWhiteSpace($fqid)) { $parts += $fqid }
        if ($null -ne $ErrorRecord.Exception) { $parts += $ErrorRecord.Exception.GetType().Name }
        $id = ($parts -join '/')
        $id = $id -replace '[^A-Za-z0-9._/-]', ''
        if ($id.Length -gt 120) { $id = $id.Substring(0, 120) }
        if ([string]::IsNullOrWhiteSpace($id)) { return $null }
        return $id
    }
    catch { return 'DiagnosticoIndisponivel' }
}

$script:Reads = New-Object System.Collections.Generic.List[object]

function Invoke-Read {
    param([string]$Capability, [string]$Command, [scriptblock]$Body, [bool]$Enumerated = $false)
    try {
        $produced = & $Body
        $items = if ($null -eq $produced) { @() } elseif ($produced -is [System.Array]) { $produced } else { , $produced }
        $script:Reads.Add(@{
            capability = $Capability; command = $Command; ok = $true; items = $items
            truncated = ($Enumerated -and ($items.Count -ge $script:EnumerationLimit))
            errorCategory = $null; errorId = $null
        })
    }
    catch {
        $script:Reads.Add(@{
            capability = $Capability; command = $Command; ok = $false; items = @(); truncated = $false
            errorCategory = (Get-ErrorCategory $_); errorId = (Get-ErrorId $_)
        })
    }
}

function Add-FailedRead {
    <# Leitura NÃO tentada porque a sessão dela não conectou: o motivo é o da conexão, declarado por leitura. #>
    param([string]$Capability, [string]$Command, [string]$Category, [string]$Id)
    $script:Reads.Add(@{
        capability = $Capability; command = $Command; ok = $false; items = @(); truncated = $false
        errorCategory = $Category; errorId = $Id
    })
}

function Get-RuleReach {
    <# Destinatários da regra que aplica a política (pessoas, grupos e domínios). #>
    param($Rule)
    if ($null -eq $Rule) { return @{ hasRule = $false; state = $null; priority = $null; sentTo = @(); sentToMemberOf = @(); recipientDomainIs = @() } }
    return @{
        hasRule           = $true
        state             = (Get-Text $Rule 'State')
        priority          = (Get-Int $Rule 'Priority')
        sentTo            = (Get-List $Rule 'SentTo')
        sentToMemberOf    = (Get-List $Rule 'SentToMemberOf')
        recipientDomainIs = (Get-List $Rule 'RecipientDomainIs')
    }
}

function Find-Rule {
    param($Rules, [string]$PolicyProperty, [string]$PolicyName)
    if ($null -eq $Rules) { return $null }
    foreach ($r in @($Rules)) {
        if ((Get-Text $r $PolicyProperty) -eq $PolicyName) { return $r }
    }
    return $null
}

# ---- Leituras do Defender para Office 365 ------------------------------------------------------------------

function Read-Defender {
    Invoke-Read 'DefenderAtpPolicy' 'Get-AtpPolicyForO365' {
        Get-AtpPolicyForO365 | ForEach-Object {
            @{
                enableATPForSPOTeamsODB = (Get-Bool $_ 'EnableATPForSPOTeamsODB')
                enableSafeDocs          = (Get-Bool $_ 'EnableSafeDocs')
                allowSafeDocsOpen       = (Get-Bool $_ 'AllowSafeDocsOpen')
            }
        }
    }

    Invoke-Read 'DefenderSafeLinks' 'Get-SafeLinksPolicy' {
        $rules = @(Get-SafeLinksRule)
        Get-SafeLinksPolicy | ForEach-Object {
            $name = Get-Text $_ 'Name'
            @{
                identity = (Get-Text $_ 'Identity'); name = $name
                enableSafeLinksForEmail  = (Get-Bool $_ 'EnableSafeLinksForEmail')
                enableSafeLinksForTeams  = (Get-Bool $_ 'EnableSafeLinksForTeams')
                enableSafeLinksForOffice = (Get-Bool $_ 'EnableSafeLinksForOffice')
                trackClicks              = (Get-Bool $_ 'TrackClicks')
                allowClickThrough        = (Get-Bool $_ 'AllowClickThrough')
                scanUrls                 = (Get-Bool $_ 'ScanUrls')
                enableForInternalSenders = (Get-Bool $_ 'EnableForInternalSenders')
                deliverMessageAfterScan  = (Get-Bool $_ 'DeliverMessageAfterScan')
                disableUrlRewrite        = (Get-Bool $_ 'DisableUrlRewrite')
                isBuiltInProtection      = (Get-Bool $_ 'IsBuiltInProtection')
                isDefault                = $false
                reach = (Get-RuleReach (Find-Rule $rules 'SafeLinksPolicy' $name))
            }
        }
    }

    Invoke-Read 'DefenderSafeAttachments' 'Get-SafeAttachmentPolicy' {
        $rules = @(Get-SafeAttachmentRule)
        Get-SafeAttachmentPolicy | ForEach-Object {
            $name = Get-Text $_ 'Name'
            @{
                identity = (Get-Text $_ 'Identity'); name = $name
                enable = (Get-Bool $_ 'Enable'); action = (Get-Text $_ 'Action')
                isBuiltInProtection = (Get-Bool $_ 'IsBuiltInProtection'); isDefault = $false
                reach = (Get-RuleReach (Find-Rule $rules 'SafeAttachmentPolicy' $name))
            }
        }
    }

    Invoke-Read 'DefenderMalwareFilter' 'Get-MalwareFilterPolicy' {
        $rules = @(Get-MalwareFilterRule)
        Get-MalwareFilterPolicy | ForEach-Object {
            $name = Get-Text $_ 'Name'
            @{
                identity = (Get-Text $_ 'Identity'); name = $name; isDefault = (Get-Bool $_ 'IsDefault')
                enableFileFilter = (Get-Bool $_ 'EnableFileFilter')
                fileTypes = (Get-List $_ 'FileTypes')
                fileTypeAction = (Get-Text $_ 'FileTypeAction')
                enableInternalSenderAdminNotifications = (Get-Bool $_ 'EnableInternalSenderAdminNotifications')
                internalSenderAdminAddressSet = (-not [string]::IsNullOrWhiteSpace((Get-Text $_ 'InternalSenderAdminAddress')))
                zapEnabled = (Get-Bool $_ 'ZapEnabled')
                reach = (Get-RuleReach (Find-Rule $rules 'MalwareFilterPolicy' $name))
            }
        }
    }

    Invoke-Read 'DefenderInboundSpam' 'Get-HostedContentFilterPolicy' {
        $rules = @(Get-HostedContentFilterRule)
        Get-HostedContentFilterPolicy | ForEach-Object {
            $name = Get-Text $_ 'Name'
            @{
                identity = (Get-Text $_ 'Identity'); name = $name; isDefault = (Get-Bool $_ 'IsDefault')
                allowedSenderDomains = (Get-List $_ 'AllowedSenderDomains')
                allowedSendersCount = @(Get-List $_ 'AllowedSenders').Count
                reach = (Get-RuleReach (Find-Rule $rules 'HostedContentFilterPolicy' $name))
            }
        }
    }

    Invoke-Read 'DefenderOutboundSpam' 'Get-HostedOutboundSpamFilterPolicy' {
        $rules = @(Get-HostedOutboundSpamFilterRule)
        Get-HostedOutboundSpamFilterPolicy | ForEach-Object {
            $name = Get-Text $_ 'Name'
            $rule = Find-Rule $rules 'HostedOutboundSpamFilterPolicy' $name
            @{
                identity = (Get-Text $_ 'Identity'); name = $name; isDefault = (Get-Bool $_ 'IsDefault')
                recipientLimitExternalPerHour = (Get-Int $_ 'RecipientLimitExternalPerHour')
                recipientLimitInternalPerHour = (Get-Int $_ 'RecipientLimitInternalPerHour')
                recipientLimitPerDay          = (Get-Int $_ 'RecipientLimitPerDay')
                actionWhenThresholdReached    = (Get-Text $_ 'ActionWhenThresholdReached')
                notifyOutboundSpam            = (Get-Bool $_ 'NotifyOutboundSpam')
                notifyOutboundSpamRecipientsCount = @(Get-List $_ 'NotifyOutboundSpamRecipients').Count
                bccSuspiciousOutboundMail     = (Get-Bool $_ 'BccSuspiciousOutboundMail')
                bccSuspiciousOutboundAdditionalRecipientsCount = @(Get-List $_ 'BccSuspiciousOutboundAdditionalRecipients').Count
                # A regra de SAÍDA alcança remetentes (From/FromMemberOf/SenderDomainIs), não destinatários.
                reach = @{
                    hasRule = ($null -ne $rule); state = (Get-Text $rule 'State'); priority = (Get-Int $rule 'Priority')
                    sentTo = (Get-List $rule 'From'); sentToMemberOf = (Get-List $rule 'FromMemberOf'); recipientDomainIs = (Get-List $rule 'SenderDomainIs')
                }
            }
        }
    }

    Invoke-Read 'DefenderConnectionFilter' 'Get-HostedConnectionFilterPolicy' {
        Get-HostedConnectionFilterPolicy | ForEach-Object {
            @{
                identity = (Get-Text $_ 'Identity'); name = (Get-Text $_ 'Name')
                ipAllowList = (Get-List $_ 'IPAllowList'); enableSafeList = (Get-Bool $_ 'EnableSafeList')
            }
        }
    }

    Invoke-Read 'DefenderAntiPhish' 'Get-AntiPhishPolicy' {
        $rules = @(Get-AntiPhishRule)
        Get-AntiPhishPolicy | ForEach-Object {
            $name = Get-Text $_ 'Name'
            @{
                identity = (Get-Text $_ 'Identity'); name = $name; isDefault = (Get-Bool $_ 'IsDefault')
                enabled = (Get-Bool $_ 'Enabled')
                phishThresholdLevel = (Get-Int $_ 'PhishThresholdLevel')
                enableTargetedUserProtection = (Get-Bool $_ 'EnableTargetedUserProtection')
                targetedUsersToProtectCount = @(Get-List $_ 'TargetedUsersToProtect').Count
                enableOrganizationDomainsProtection = (Get-Bool $_ 'EnableOrganizationDomainsProtection')
                enableMailboxIntelligence = (Get-Bool $_ 'EnableMailboxIntelligence')
                enableMailboxIntelligenceProtection = (Get-Bool $_ 'EnableMailboxIntelligenceProtection')
                enableSpoofIntelligence = (Get-Bool $_ 'EnableSpoofIntelligence')
                targetedUserProtectionAction = (Get-Text $_ 'TargetedUserProtectionAction')
                targetedDomainProtectionAction = (Get-Text $_ 'TargetedDomainProtectionAction')
                mailboxIntelligenceProtectionAction = (Get-Text $_ 'MailboxIntelligenceProtectionAction')
                reach = (Get-RuleReach (Find-Rule $rules 'AntiPhishPolicy' $name))
            }
        }
    }

    Invoke-Read 'DefenderDkim' 'Get-DkimSigningConfig' {
        Get-DkimSigningConfig | ForEach-Object {
            @{ domain = (Get-Text $_ 'Domain'); enabled = (Get-Bool $_ 'Enabled'); status = (Get-Text $_ 'Status') }
        }
    }

    Invoke-Read 'DefenderAcceptedDomains' 'Get-AcceptedDomain' {
        Get-AcceptedDomain | ForEach-Object {
            @{ domainName = (Get-Text $_ 'DomainName'); domainType = (Get-Text $_ 'DomainType'); isDefault = (Get-Bool $_ 'Default') }
        }
    }

    Invoke-Read 'DefenderTeamsProtection' 'Get-TeamsProtectionPolicy' {
        Get-TeamsProtectionPolicy | ForEach-Object {
            @{ identity = (Get-Text $_ 'Identity'); zapEnabled = (Get-Bool $_ 'ZapEnabled') }
        }
    }

    Invoke-Read 'DefenderPriorityAccounts' 'Get-EmailTenantSettings' {
        $settings = Get-EmailTenantSettings | Select-Object -First 1
        $vips = @(Get-User -IsVIP -ResultSize $script:EnumerationLimit -WarningAction SilentlyContinue | ForEach-Object {
            @{
                externalDirectoryObjectId = (Get-Text $_ 'ExternalDirectoryObjectId')
                userPrincipalName = (Get-Text $_ 'UserPrincipalName')
                displayName = (Get-Text $_ 'DisplayName')
            }
        })
        @{
            enablePriorityAccountProtection = (Get-Bool $settings 'EnablePriorityAccountProtection')
            accounts = $vips
            listComplete = ($vips.Count -lt $script:EnumerationLimit)
        }
    }

    Invoke-Read 'DefenderPresetPolicies' 'Get-EOPProtectionPolicyRule' {
        $all = @()
        foreach ($kind in @('EOP', 'ATP')) {
            $cmd = if ($kind -eq 'EOP') { 'Get-EOPProtectionPolicyRule' } else { 'Get-ATPProtectionPolicyRule' }
            $all += @(& $cmd | ForEach-Object {
                @{
                    kind = $kind; identity = (Get-Text $_ 'Identity'); name = (Get-Text $_ 'Name'); state = (Get-Text $_ 'State')
                    priority = (Get-Int $_ 'Priority'); sentTo = (Get-List $_ 'SentTo'); sentToMemberOf = (Get-List $_ 'SentToMemberOf')
                    recipientDomainIs = (Get-List $_ 'RecipientDomainIs')
                }
            })
        }
        $all
    }
}

# ---- Leituras do Purview ----------------------------------------------------------------------------------

function Read-PurviewExchange {
    Invoke-Read 'PurviewAuditConfig' 'Get-AdminAuditLogConfig' {
        Get-AdminAuditLogConfig | ForEach-Object {
            @{ unifiedAuditLogIngestionEnabled = (Get-Bool $_ 'UnifiedAuditLogIngestionEnabled') }
        }
    }
}

function Read-PurviewCompliance {
    Invoke-Read 'PurviewDlpPolicies' 'Get-DlpCompliancePolicy' {
        Get-DlpCompliancePolicy | ForEach-Object {
            @{
                identity = (Get-Text $_ 'Identity'); name = (Get-Text $_ 'Name'); mode = (Get-Text $_ 'Mode')
                enabled = (Get-Bool $_ 'Enabled'); workloads = (Get-List $_ 'Workload')
                exchangeLocation = (Get-List $_ 'ExchangeLocation'); sharePointLocation = (Get-List $_ 'SharePointLocation')
                oneDriveLocation = (Get-List $_ 'OneDriveLocation'); teamsLocation = (Get-List $_ 'TeamsLocation')
                enforcementPlanes = (Get-List $_ 'EnforcementPlanes'); applicationLocations = (Get-List $_ 'Locations')
            }
        }
    }

    Invoke-Read 'PurviewLabelPolicies' 'Get-LabelPolicy' {
        Get-LabelPolicy | ForEach-Object {
            $locations = 0
            foreach ($p in @('ExchangeLocation', 'ModernGroupLocation', 'SharePointLocation', 'OneDriveLocation', 'PublicFolderLocation', 'SkypeLocation')) {
                $locations += @(Get-List $_ $p).Count
            }
            @{
                identity = (Get-Text $_ 'Identity'); name = (Get-Text $_ 'Name'); enabled = (Get-Bool $_ 'Enabled')
                mode = (Get-Text $_ 'Mode'); labels = (Get-List $_ 'Labels'); locationCount = $locations
            }
        }
    }
}

# ---- Execução --------------------------------------------------------------------------------------------

$script:EnumerationLimit = 5000

$result = @{
    runtime = @{ powerShell = $null; module = $null; platform = $null }
    connected = $false; connectionErrorId = $null; connectionErrorCategory = $null
    complianceConnected = $false; complianceConnectionErrorCategory = $null; complianceConnectionErrorId = $null
    enumerationLimit = $script:EnumerationLimit
    reads = @()
}

try {
    $result.runtime.powerShell = [string]$PSVersionTable.PSVersion
    $result.runtime.platform = [string]$PSVersionTable.Platform

    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { throw 'Nenhuma credencial recebida na entrada padrão.' }
    $request = $raw | ConvertFrom-Json

    Import-Module ExchangeOnlineManagement -ErrorAction Stop
    $module = Get-Module ExchangeOnlineManagement
    if ($null -ne $module) { $result.runtime.module = [string]$module.Version }

    $mode = Get-Prop $request 'mode'
    $profileName = [string](Get-Prop $request 'profile')
    if ($mode -eq 'module-check') {
        foreach ($n in @('Connect-ExchangeOnline', 'Connect-IPPSSession', 'Disconnect-ExchangeOnline')) {
            $found = $null -ne (Get-Command $n -Module ExchangeOnlineManagement -ErrorAction SilentlyContinue)
            $script:Reads.Add(@{ capability = 'ModuleCheck'; command = $n; ok = $found; items = @(); truncated = $false
                errorCategory = $(if ($found) { $null } else { 'Unavailable' }); errorId = $(if ($found) { $null } else { 'CommandNotFound' }) })
        }
        $result.reads = $script:Reads.ToArray()
        $result | ConvertTo-Json -Depth 12 -Compress
        exit 0
    }

    $limit = Get-Prop $request 'enumerationLimit'
    if ($null -ne $limit) {
        $parsed = 0
        if ([int]::TryParse(([string]$limit).Trim(), [ref]$parsed) -and $parsed -gt 0 -and $parsed -le 50000) {
            $script:EnumerationLimit = $parsed; $result.enumerationLimit = $parsed
        }
    }

    $accessToken = [string](Get-Prop $request 'accessToken')
    $organization = [string](Get-Prop $request 'organization')
    if ([string]::IsNullOrWhiteSpace($accessToken)) { throw 'Token de acesso ausente na entrada.' }
    if ([string]::IsNullOrWhiteSpace($organization)) { throw 'Domínio da organização ausente na entrada.' }
    $testOnly = ($mode -eq 'test')

    $exoCommands = if ($profileName -eq 'purview') { $script:PurviewExchangeCommands }
                   elseif ($testOnly) { @('Get-AtpPolicyForO365') }
                   else { $script:DefenderCommands }

    try {
        Connect-ExchangeOnline -AccessToken $accessToken -Organization $organization -CommandName $exoCommands `
            -ShowBanner:$false -SkipLoadingFormatData -ErrorAction Stop | Out-Null
        $result.connected = $true
    }
    catch {
        $result.connected = $false
        $result.connectionErrorCategory = Get-ErrorCategory $_
        $result.connectionErrorId = Get-ErrorId $_
    }

    if ($result.connected) {
        try {
            if ($profileName -eq 'purview') { Read-PurviewExchange }
            elseif ($testOnly) { Invoke-Read 'DefenderAtpPolicy' 'Get-AtpPolicyForO365' { Get-AtpPolicyForO365 | ForEach-Object { @{ ok = $true } } } }
            else { Read-Defender }
        }
        finally {
            try { Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue | Out-Null } catch { }
        }
    }

    if ($profileName -eq 'purview') {
        # A sessão do Security & Compliance é OUTRA conexão, com OUTRO token. A falha dela não apaga a leitura do log
        # de auditoria, e as leituras que dependem dela registram o motivo da conexão.
        $complianceToken = [string](Get-Prop $request 'complianceToken')
        try {
            if ([string]::IsNullOrWhiteSpace($complianceToken)) { throw 'Token do Security & Compliance ausente.' }
            Connect-IPPSSession -AccessToken $complianceToken -Organization $organization -CommandName $script:PurviewComplianceCommands `
                -ShowBanner:$false -ErrorAction Stop | Out-Null
            $result.complianceConnected = $true
        }
        catch {
            $result.complianceConnectionErrorCategory = Get-ErrorCategory $_
            $result.complianceConnectionErrorId = Get-ErrorId $_
        }

        if ($result.complianceConnected) {
            try { if (-not $testOnly) { Read-PurviewCompliance } else { Invoke-Read 'PurviewDlpPolicies' 'Get-DlpCompliancePolicy' { @(Get-DlpCompliancePolicy | Select-Object -First 1 | ForEach-Object { @{ ok = $true } }) } } }
            finally { try { Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue | Out-Null } catch { } }
        }
        else {
            Add-FailedRead 'PurviewDlpPolicies' 'Get-DlpCompliancePolicy' $result.complianceConnectionErrorCategory $result.complianceConnectionErrorId
            if (-not $testOnly) {
                Add-FailedRead 'PurviewLabelPolicies' 'Get-LabelPolicy' $result.complianceConnectionErrorCategory $result.complianceConnectionErrorId
            }
        }
    }

    $result.reads = $script:Reads.ToArray()
    $result | ConvertTo-Json -Depth 12 -Compress
    exit 0
}
catch {
    $registro = $_
    try {
        $result.connected = $false
        $result.connectionErrorCategory = Get-ErrorCategory $registro
        $result.connectionErrorId = Get-ErrorId $registro
        $result.reads = $script:Reads.ToArray()
        $result | ConvertTo-Json -Depth 12 -Compress
    }
    catch {
        '{"runtime":{},"connected":false,"connectionErrorCategory":"Error","connectionErrorId":"DocumentoDeResultadoIndisponivel","reads":[]}'
    }
    exit 0
}
