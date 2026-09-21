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
    não interrompe as outras nem contamina o resultado delas.

    DIAGNÓSTICO DE FALHA — o texto BRUTO da exceção NÃO atravessa esta fronteira. Truncar não é sanitizar: uma
    mensagem de erro pode repetir o cabeçalho de autorização, o token que a originou ou o corpo da resposta, e
    um corte por tamanho pode deixar um pedaço disso para trás. O que sai daqui é estruturado e controlado:
      • errorCategory — a classificação (permissão, autenticação, limite de taxa, licença, indisponibilidade);
      • errorId       — o identificador de erro do PowerShell e o tipo da exceção, restritos a um conjunto FIXO
                        de caracteres e limitados em tamanho, para diagnóstico técnico.
    A mensagem que o cliente lê é montada pelo AEGIS a partir da CATEGORIA e do COMANDO, nunca do texto da fonte.

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
    # A classificação NUNCA pode falhar: ela roda no caminho de ERRO, e uma exceção aqui destruiria o documento
    # de resultado inteiro — o processo terminaria sem saída e o AEGIS veria "falha de transporte" no lugar de
    # uma leitura declarada com motivo. Por isso tudo aqui é defensivo.
    try {
        if ($null -eq $ErrorRecord) { return 'Error' }
        $ex = $ErrorRecord.Exception
        if ($null -eq $ex) { return 'Error' }
        if ($ex -is [System.Management.Automation.CommandNotFoundException]) { return 'Unavailable' }
        $m = [string]$ex.Message
        if ([string]::IsNullOrEmpty($m)) { return 'Error' }
        if ($m -match '(?i)429|throttl|too many requests') { return 'Throttled' }
        if ($m -match '(?i)\b401\b|unauthorized|AADSTS|invalid.?token|expired') { return 'AuthenticationFailure' }
        if ($m -match '(?i)\b403\b|forbidden|access denied|not authorized|insufficient|privileg') { return 'InsufficientPermission' }
        if ($m -match '(?i)\b50[0234]\b|service unavailable|timed out|timeout|temporarily') { return 'Unavailable' }
        if ($m -match '(?i)licen[sc]') { return 'LimitedByLicense' }
        return 'Error'
    }
    catch { return 'Error' }
}

function Get-ErrorId {
    <#
        Identificador TÉCNICO do erro, para diagnóstico — e nada além disso. NÃO usa a mensagem da exceção:
        mensagem é texto livre da fonte e pode repetir cabeçalho de autorização, token ou corpo de resposta.
        Usa o identificador de erro do PowerShell e o nome do tipo da exceção, ambos filtrados para um conjunto
        FIXO de caracteres (letras, dígitos, ponto, traço, barra, sublinhado) e limitados em tamanho.
    #>
    param([System.Management.Automation.ErrorRecord]$ErrorRecord)
    # Mesma regra da classificação: roda no caminho de erro e não pode falhar.
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
            errorId       = $null
        })
    }
    catch {
        $script:Reads.Add(@{
            capability    = $Capability
            command       = $Command
            ok            = $false
            items         = @()
            errorCategory = (Get-ErrorCategory $_)
            errorId       = (Get-ErrorId $_)
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

function Read-AppAvailability {
    <#
        Modelo de DISPONIBILIDADE DE APLICATIVOS do gerenciamento centrado em aplicativos (ACM) / unificado (UAM).
        É o que permite dizer QUAL modelo governa o acesso a aplicativos: a documentação oficial do
        Get-CsTeamsAppPermissionPolicy declara que ele "só é aplicável a locatários que NÃO foram migrados para
        ACM ou UAM", e a do ACM declara que, depois da migração, as políticas de permissão não podem mais ser
        acessadas, editadas nem usadas.

        O resultado é AGREGADO aqui, de propósito: o comando devolve, por aplicativo, o identificador e o
        AssignedBy (o identificador de quem fez a última alteração — dado pessoal). Nada disso é necessário para
        decidir qual modelo governa, então nada disso sai deste processo. Só contagens atravessam.
    #>
    Invoke-Read 'TeamsAppAvailability' 'Get-AllM365TeamsApps' {
        $apps = @(Get-AllM365TeamsApps)
        $total = $apps.Count
        $withAssignment = 0
        $everyone = 0
        $usersAndGroups = 0
        $noOne = 0

        foreach ($app in $apps) {
            $availability = Get-Prop $app 'AvailableTo'
            if ($null -eq $availability) { continue }
            $type = Get-Text $availability 'AssignmentType'
            if ($null -eq $type) { continue }
            $withAssignment++
            switch -Regex ($type) {
                '^(?i)everyone$' { $everyone++ }
                '^(?i)usersandgroups$' { $usersAndGroups++ }
                '^(?i)noone$' { $noOne++ }
            }
        }

        @{
            appsRead                = $total
            appsWithAssignment      = $withAssignment
            assignedToEveryone      = $everyone
            assignedToUsersAndGroups = $usersAndGroups
            assignedToNoOne         = $noOne
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
    runtime           = @{ powerShell = $null; module = $null; platform = $null }
    connected         = $false
    connectionErrorId = $null
    connectionErrorCategory = $null
    reads             = @()
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
            'Get-CsTeamsAppPermissionPolicy', 'Get-AllM365TeamsApps', 'Get-CsGroupPolicyAssignment')
        foreach ($n in $names) {
            $found = $null -ne (Get-Command $n -Module MicrosoftTeams -ErrorAction SilentlyContinue)
            $script:Reads.Add(@{
                capability = 'ModuleCheck'; command = $n; ok = $found; items = @()
                errorCategory = $(if ($found) { $null } else { 'Unavailable' })
                errorId = $(if ($found) { $null } else { 'CommandNotFound' })
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
        $result.connectionErrorId = Get-ErrorId $_
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
        Read-AppAvailability
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
    # ÚLTIMA LINHA DE DEFESA. Este bloco roda quando algo já deu errado, e ele próprio pode falhar: sob
    # Set-StrictMode, uma propriedade ausente num objeto de erro inesperado derrubaria o script aqui dentro. Se
    # isso acontecesse, o processo terminaria SEM NENHUMA SAÍDA — e o AEGIS veria apenas "terminou com código 1",
    # sem categoria nem motivo. Por isso a montagem do documento é protegida e, no pior caso, um documento
    # MÍNIMO é escrito à mão: o contrato de saída é sempre honrado.
    $registro = $_
    try {
        $result.connected = $false
        $result.connectionErrorCategory = Get-ErrorCategory $registro
        $result.connectionErrorId = Get-ErrorId $registro
        $result.reads = @($script:Reads)
        $result | ConvertTo-Json -Depth 12 -Compress
    }
    catch {
        '{"runtime":{},"connected":false,"connectionErrorCategory":"Error","connectionErrorId":"DocumentoDeResultadoIndisponivel","reads":[]}'
    }
    exit 0
}
