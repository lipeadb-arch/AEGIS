#Requires -Version 7.2
<#
    [AEGIS-KNIGHT-COVERAGE-02] Adaptador de COLETA do Microsoft Teams — somente leitura.

    Este arquivo é um RECURSO EMBUTIDO do AEGIS e é a ÚNICA coisa que o processo do PowerShell executa. O
    cliente não fornece script, comando, parâmetro nem endereço de destino: os comandos abaixo são fixos e
    todos de leitura (verbo Get). Nada aqui altera configuração do locatário.

    Entrada: UMA linha em JSON na entrada padrão, com os tokens de acesso. Os tokens NÃO passam por argumento
    de linha de comando nem por variável de ambiente — em Linux a linha de comando de um processo é legível por
    outros processos, e o ambiente de um processo aparece no diagnóstico de falhas.

    Saída: UM documento JSON na saída padrão. Cada leitura é registrada com o desfecho próprio: a falha de uma
    não interrompe as outras nem contamina o resultado delas. Mensagens de erro são resumidas e classificadas;
    o texto bruto do erro é truncado e nunca carrega token.

    Autenticação: Connect-MicrosoftTeams com -AccessTokens, o caminho de autenticação de APLICATIVO documentado
    pela Microsoft. São necessários DOIS tokens, de RECURSOS DIFERENTES (Microsoft Graph e a API de
    administração do Teams) — um token não é reaproveitado no outro recurso.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# ---- Utilitários -----------------------------------------------------------------------------------------

function Get-Prop {
    <# Propriedade que pode não existir na versão instalada do módulo: ausente devolve $null, nunca erro. #>
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
    $s = [string]$v
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

function Get-Count {
    param($Object, [string]$Name)
    $v = Get-Prop $Object $Name
    if ($null -eq $v) { return 0 }
    return @($v).Count
}

function Get-ErrorCategory {
    <#
        Classificação HEURÍSTICA da falha, para que o AEGIS separe permissão de indisponibilidade em vez de
        colapsar tudo em "erro". O texto do erro não é reproduzido no relatório; só esta categoria e um resumo.
    #>
    param([System.Management.Automation.ErrorRecord]$ErrorRecord)
    if ($null -eq $ErrorRecord) { return 'Error' }
    if ($ErrorRecord.Exception -is [System.Management.Automation.CommandNotFoundException]) { return 'Unavailable' }
    $m = [string]$ErrorRecord.Exception.Message
    if ($m -match '(?i)429|throttl|too many requests') { return 'Throttled' }
    if ($m -match '(?i)\b401\b|unauthorized|AADSTS|invalid.?token|expired') { return 'AuthenticationFailure' }
    if ($m -match '(?i)\b403\b|forbidden|access denied|not authorized|insufficient|privileg') { return 'InsufficientPermission' }
    if ($m -match '(?i)\b50[0234]\b|service unavailable|timed out|timeout|temporarily') { return 'Unavailable' }
    if ($m -match '(?i)licen[sc]') { return 'LimitedByLicense' }
    return 'Error'
}

function Get-ErrorSummary {
    param([System.Management.Automation.ErrorRecord]$ErrorRecord)
    if ($null -eq $ErrorRecord) { return $null }
    $m = ([string]$ErrorRecord.Exception.Message) -replace '\s+', ' '
    $m = $m.Trim()
    if ($m.Length -gt 300) { $m = $m.Substring(0, 300) + '…' }
    return $m
}

$script:Reads = New-Object System.Collections.Generic.List[object]

function Invoke-Read {
    <# Executa UMA leitura fixa e registra o desfecho. Uma falha aqui nunca interrompe as demais. #>
    param([string]$Capability, [string]$Command, [scriptblock]$Body)
    try {
        $items = @(& $Body)
        $script:Reads.Add(@{
            capability    = $Capability
            command       = $Command
            ok            = $true
            items         = $items
            errorCategory = $null
            error         = $null
        })
    }
    catch {
        $script:Reads.Add(@{
            capability    = $Capability
            command       = $Command
            ok            = $false
            items         = @()
            errorCategory = (Get-ErrorCategory $_)
            error         = (Get-ErrorSummary $_)
        })
    }
}

# ---- Leituras (fixas, todas de verbo Get) ----------------------------------------------------------------

function Read-ClientConfiguration {
    Invoke-Read 'TeamsClientConfiguration' 'Get-CsTeamsClientConfiguration' {
        Get-CsTeamsClientConfiguration | ForEach-Object {
            @{
                identity             = (Get-Text $_ 'Identity')
                allowEmailIntoChannel = (Get-Bool $_ 'AllowEmailIntoChannel')
                allowDropBox         = (Get-Bool $_ 'AllowDropBox')
                allowBox             = (Get-Bool $_ 'AllowBox')
                allowGoogleDrive     = (Get-Bool $_ 'AllowGoogleDrive')
                allowShareFile       = (Get-Bool $_ 'AllowShareFile')
                allowEgnyte          = (Get-Bool $_ 'AllowEgnyte')
                allowGuestUser       = (Get-Bool $_ 'AllowGuestUser')
            }
        }
    }
}

function Read-FederationConfiguration {
    Invoke-Read 'TeamsFederationConfiguration' 'Get-CsTenantFederationConfiguration' {
        Get-CsTenantFederationConfiguration | ForEach-Object {
            $fed = $_

            # A lista de domínios permitidos é um objeto de escolha: "todos os conhecidos" ou uma lista fechada.
            # A forma é lida do próprio tipo devolvido pelo módulo — não há endereço nem contrato interno aqui.
            $choice = Get-Prop $fed 'AllowedDomains'
            $kind = $null
            $allowed = @()
            if ($null -ne $choice) {
                $typeName = $choice.GetType().Name
                if ($typeName -like 'AllowAllKnownDomains*') {
                    $kind = 'AllowAllKnownDomains'
                }
                elseif ($typeName -like 'AllowList*') {
                    $kind = 'AllowList'
                    $allowed = @(Get-Prop $choice 'AllowedDomain' | ForEach-Object { Get-Text $_ 'Domain' } | Where-Object { $_ })
                }
                else {
                    $kind = $typeName
                }
            }

            $asList = @(Get-Prop $fed 'AllowedDomainsAsAList' | ForEach-Object { [string]$_ } | Where-Object { $_ })
            if ($asList.Count -gt 0) {
                if ($null -eq $kind) { $kind = 'AllowList' }
                $allowed = @(@($allowed) + $asList | Select-Object -Unique)
            }

            $blocked = @(Get-Prop $fed 'BlockedDomains' | ForEach-Object {
                    $d = Get-Text $_ 'Domain'
                    if ($null -eq $d) { [string]$_ } else { $d }
                } | Where-Object { $_ })

            @{
                allowFederatedUsers                        = (Get-Bool $fed 'AllowFederatedUsers')
                allowedDomainsKind                         = $kind
                allowedDomains                             = $allowed
                blockedDomains                             = $blocked
                blockAllSubdomains                         = (Get-Bool $fed 'BlockAllSubdomains')
                allowTeamsConsumer                         = (Get-Bool $fed 'AllowTeamsConsumer')
                allowTeamsConsumerInbound                  = (Get-Bool $fed 'AllowTeamsConsumerInbound')
                externalAccessWithTrialTenants             = (Get-Text $fed 'ExternalAccessWithTrialTenants')
                allowedTrialTenantDomains                  = @(Get-Prop $fed 'AllowedTrialTenantDomains' | ForEach-Object { [string]$_ } | Where-Object { $_ })
                restrictTeamsConsumerToExternalUserProfiles = (Get-Bool $fed 'RestrictTeamsConsumerToExternalUserProfiles')
            }
        }
    }
}

function Read-MeetingPolicies {
    Invoke-Read 'TeamsMeetingPolicies' 'Get-CsTeamsMeetingPolicy' {
        Get-CsTeamsMeetingPolicy | ForEach-Object {
            @{
                identity                                 = (Get-Text $_ 'Identity')
                allowAnonymousUsersToJoinMeeting         = (Get-Bool $_ 'AllowAnonymousUsersToJoinMeeting')
                allowAnonymousUsersToStartMeeting        = (Get-Bool $_ 'AllowAnonymousUsersToStartMeeting')
                autoAdmittedUsers                        = (Get-Text $_ 'AutoAdmittedUsers')
                allowPSTNUsersToBypassLobby              = (Get-Bool $_ 'AllowPSTNUsersToBypassLobby')
                meetingChatEnabledType                   = (Get-Text $_ 'MeetingChatEnabledType')
                designatedPresenterRoleMode              = (Get-Text $_ 'DesignatedPresenterRoleMode')
                allowExternalParticipantGiveRequestControl = (Get-Bool $_ 'AllowExternalParticipantGiveRequestControl')
                allowExternalNonTrustedMeetingChat       = (Get-Bool $_ 'AllowExternalNonTrustedMeetingChat')
                allowCloudRecording                      = (Get-Bool $_ 'AllowCloudRecording')
            }
        }
    }
}

function Read-MessagingPolicies {
    Invoke-Read 'TeamsMessagingPolicies' 'Get-CsTeamsMessagingPolicy' {
        Get-CsTeamsMessagingPolicy | ForEach-Object {
            @{
                identity                      = (Get-Text $_ 'Identity')
                allowSecurityEndUserReporting = (Get-Bool $_ 'AllowSecurityEndUserReporting')
            }
        }
    }
}

function Read-AppPermissionPolicies {
    Invoke-Read 'TeamsAppPermissionPolicies' 'Get-CsTeamsAppPermissionPolicy' {
        Get-CsTeamsAppPermissionPolicy | ForEach-Object {
            @{
                identity               = (Get-Text $_ 'Identity')
                defaultCatalogAppsType = (Get-Text $_ 'DefaultCatalogAppsType')
                defaultCatalogAppsCount = (Get-Count $_ 'DefaultCatalogApps')
                globalCatalogAppsType  = (Get-Text $_ 'GlobalCatalogAppsType')
                globalCatalogAppsCount = (Get-Count $_ 'GlobalCatalogApps')
                privateCatalogAppsType = (Get-Text $_ 'PrivateCatalogAppsType')
                privateCatalogAppsCount = (Get-Count $_ 'PrivateCatalogApps')
            }
        }
    }
}

function Read-PolicyAssignments {
    Invoke-Read 'TeamsPolicyAssignments' 'Get-CsGroupPolicyAssignment' {
        Get-CsGroupPolicyAssignment | ForEach-Object {
            @{
                policyType = (Get-Text $_ 'PolicyType')
                policyName = (Get-Text $_ 'PolicyName')
                groupId    = (Get-Text $_ 'GroupId')
                rank       = (Get-Prop $_ 'Rank')
            }
        }
    }
}

# ---- Execução --------------------------------------------------------------------------------------------

$result = @{
    runtime          = @{ powerShell = $null; module = $null; platform = $null }
    connected        = $false
    connectionError  = $null
    connectionErrorCategory = $null
    reads            = @()
}

try {
    $result.runtime.powerShell = [string]$PSVersionTable.PSVersion
    $result.runtime.platform = [string]$PSVersionTable.Platform

    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { throw 'Nenhuma credencial recebida na entrada padrão.' }
    $request = $raw | ConvertFrom-Json

    Import-Module MicrosoftTeams -ErrorAction Stop
    $module = Get-Module MicrosoftTeams
    if ($null -ne $module) { $result.runtime.module = [string]$module.Version }

    $mode = Get-Prop $request 'mode'
    if ($mode -eq 'module-check') {
        # Validação de RUNTIME: prova que o módulo importa e que os comandos existem nesta plataforma.
        # Não conecta em locatário nenhum e não precisa de credencial.
        $result.connected = $false
        $names = @(
            'Connect-MicrosoftTeams', 'Disconnect-MicrosoftTeams', 'Get-CsTeamsClientConfiguration',
            'Get-CsTenantFederationConfiguration', 'Get-CsTeamsMeetingPolicy', 'Get-CsTeamsMessagingPolicy',
            'Get-CsTeamsAppPermissionPolicy', 'Get-CsGroupPolicyAssignment')
        foreach ($n in $names) {
            $found = $null -ne (Get-Command $n -Module MicrosoftTeams -ErrorAction SilentlyContinue)
            $script:Reads.Add(@{
                capability = 'ModuleCheck'; command = $n; ok = $found; items = @()
                errorCategory = $(if ($found) { $null } else { 'Unavailable' })
                error = $(if ($found) { $null } else { 'Comando ausente nesta versão do módulo.' })
            })
        }
        $result.reads = @($script:Reads)
        $result | ConvertTo-Json -Depth 12 -Compress
        exit 0
    }

    $graphToken = [string](Get-Prop $request 'graphToken')
    $teamsToken = [string](Get-Prop $request 'teamsToken')
    if ([string]::IsNullOrWhiteSpace($graphToken) -or [string]::IsNullOrWhiteSpace($teamsToken)) {
        throw 'Tokens de acesso ausentes na entrada.'
    }

    try {
        Connect-MicrosoftTeams -AccessTokens @($graphToken, $teamsToken) -ErrorAction Stop | Out-Null
        $result.connected = $true
    }
    catch {
        $result.connected = $false
        $result.connectionErrorCategory = Get-ErrorCategory $_
        $result.connectionError = Get-ErrorSummary $_
        $result.reads = @()
        $result | ConvertTo-Json -Depth 12 -Compress
        exit 0
    }

    try {
        Read-ClientConfiguration
        Read-FederationConfiguration
        Read-MeetingPolicies
        Read-MessagingPolicies
        Read-AppPermissionPolicies
        Read-PolicyAssignments
    }
    finally {
        # A sessão é encerrada SEMPRE: o processo é descartável, mas a conexão com o locatário não pode
        # sobreviver à coleta nem ser reaproveitada por outra.
        try { Disconnect-MicrosoftTeams -Confirm:$false -ErrorAction SilentlyContinue | Out-Null } catch { }
    }

    $result.reads = @($script:Reads)
    $result | ConvertTo-Json -Depth 12 -Compress
    exit 0
}
catch {
    $result.connected = $false
    $result.connectionErrorCategory = Get-ErrorCategory $_
    $result.connectionError = Get-ErrorSummary $_
    $result.reads = @($script:Reads)
    $result | ConvertTo-Json -Depth 12 -Compress
    exit 0
}
