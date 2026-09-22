#Requires -Version 7.2
<#
    [AEGIS-KNIGHT-COVERAGE-03] Adaptador de COLETA do Exchange Online — somente leitura.

    Este arquivo é um RECURSO EMBUTIDO do AEGIS e é a ÚNICA coisa que o processo do PowerShell executa. O
    cliente não fornece script, comando, parâmetro nem endereço de destino: os comandos abaixo são fixos e
    todos de leitura (verbo Get). Nada aqui altera configuração do locatário.

    Entrada: UMA linha em JSON na entrada padrão, com o token de acesso e o domínio da organização. O token NÃO
    passa por argumento de linha de comando nem por variável de ambiente — em Linux a linha de comando de um
    processo é legível por outros processos, e o ambiente aparece no diagnóstico de falhas.

    Saída: UM documento JSON na saída padrão. Cada leitura é registrada com o desfecho próprio: a falha de uma
    não interrompe as outras nem contamina o resultado delas.

    DIAGNÓSTICO DE FALHA — o texto BRUTO da exceção NÃO atravessa esta fronteira (mesma regra do adaptador do
    Teams, e pelo mesmo motivo: truncar não é sanitizar). O que sai daqui é `errorCategory` (a classificação) e
    `errorId` (identificador técnico restrito a um conjunto FIXO de caracteres). A mensagem que o cliente lê é
    montada pelo AEGIS a partir da CATEGORIA e do COMANDO.

    AUTENTICAÇÃO — decisão do bloco, para não ser redescoberta:
      • `Connect-ExchangeOnline -AccessToken` é parâmetro DOCUMENTADO do módulo (disponível a partir da versão
        3.1.0-Preview1) e, para token de APLICATIVO, a documentação manda usá-lo junto de `-Organization`.
      • O token tem de ser emitido para o recurso `https://outlook.office365.com` — NÃO serve o token do
        Microsoft Graph nem o da administração do Teams.
      • A AUTORIZAÇÃO da sessão não vem do parâmetro: a documentação de autenticação de aplicativo diz que o
        papel de diretório atribuído à aplicação é devolvido DENTRO do token e é dele que o controle de acesso
        da sessão é montado. Por isso `Exchange.ManageAsApp` sozinho não basta — sem papel de diretório a
        conexão é recusada.
      • NÃO é necessário certificado: o token é obtido por credenciais de cliente com o segredo que o conector
        Microsoft já guarda. O certificado é uma forma de obter o token, não um requisito do Exchange.

    ENUMERAÇÃO — caixas de correio, contas e associações são lidas com TETO explícito (`-ResultSize`). Quando o
    teto é atingido, a leitura é marcada como TRUNCADA e o AEGIS impede que ela sustente a aprovação da
    organização inteira. Uma enumeração limitada que não encontrou problema não é ambiente sem problema.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# ---- Contrato de comandos --------------------------------------------------------------------------------
#
# Dois conjuntos, e a distinção é o ponto: os comandos do MÓDULO existem assim que ele é importado; os
# comandos de SESSÃO só passam a existir depois de uma conexão bem-sucedida com o locatário, porque é a
# conexão que os importa. Uma verificação OFFLINE pode provar o primeiro conjunto e NÃO pode provar o segundo —
# e muito menos a autorização para executá-los. Tratar os dois como a mesma coisa produziria um gate que
# "aprova" um ambiente em que nenhuma leitura funcionaria.

$script:ModuleCommands = @(
    'Connect-ExchangeOnline',
    'Disconnect-ExchangeOnline',
    'Get-EXOMailbox'
)

$script:SessionCommands = @(
    'Get-OrganizationConfig',
    'Get-TransportConfig',
    'Get-SharingPolicy',
    'Get-OwaMailboxPolicy',
    'Get-TransportRule',
    'Get-RoleAssignmentPolicy',
    'Get-ExternalInOutlook',
    'Get-HostedOutboundSpamFilterPolicy',
    'Get-Mailbox',
    'Get-User',
    'Get-CASMailbox',
    'Get-MailboxAuditBypassAssociation'
)

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
    <# Coleção de textos, sempre um array (vazio quando ausente). Valores em branco são descartados. #>
    param($Object, [string]$Name)
    $v = Get-Prop $Object $Name
    if ($null -eq $v) { return @() }
    return @($v | ForEach-Object { ([string]$_).Trim() } | Where-Object { $_ -ne '' })
}

function Get-ErrorCategory {
    <#
        Classificação HEURÍSTICA da falha, para que o AEGIS separe permissão de indisponibilidade em vez de
        colapsar tudo em "erro". O texto do erro não é reproduzido no relatório; só esta categoria e um resumo.
    #>
    param([System.Management.Automation.ErrorRecord]$ErrorRecord)
    # A classificação NUNCA pode falhar: ela roda no caminho de ERRO, e uma exceção aqui destruiria o documento
    # de resultado inteiro — o processo terminaria sem saída e o AEGIS veria "falha de transporte" no lugar de
    # uma leitura declarada com motivo.
    try {
        if ($null -eq $ErrorRecord) { return 'Error' }
        $ex = $ErrorRecord.Exception
        if ($null -eq $ex) { return 'Error' }
        if ($ex -is [System.Management.Automation.CommandNotFoundException]) { return 'Unavailable' }
        $m = [string]$ex.Message
        if ([string]::IsNullOrEmpty($m)) { return 'Error' }
        if ($m -match '(?i)429|throttl|too many requests|micro delay') { return 'Throttled' }
        if ($m -match '(?i)\b401\b|unauthorized|AADSTS|invalid.?token|expired') { return 'AuthenticationFailure' }
        # "role assigned to application isn't supported in this scenario" é a recusa por FALTA DE PAPEL de
        # diretório — autorização, não autenticação. Classificar como permissão insuficiente é o que faz o
        # AEGIS dizer ao operador a coisa certa a corrigir.
        if ($m -match '(?i)\b403\b|forbidden|access denied|not authorized|insufficient|privileg|role assigned') { return 'InsufficientPermission' }
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
    #>
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
    <#
        Executa UMA leitura fixa e registra o desfecho. Uma falha aqui nunca interrompe as demais.
        `$Truncated` diz se a enumeração atingiu o teto — informação que o AEGIS usa para impedir que uma
        leitura parcial aprove a organização inteira.
    #>
    param([string]$Capability, [string]$Command, [scriptblock]$Body, [bool]$Enumerated = $false)
    try {
        $produced = & $Body
        # `@()` sobre uma List[object] com hashtables lança "Argument types do not match" no PowerShell 7.6.5.
        # Materializar com .ToArray()/cast explícito é o caminho que funciona nas duas famílias de versão.
        $items = if ($null -eq $produced) { @() } elseif ($produced -is [System.Array]) { $produced } else { , $produced }
        $script:Reads.Add(@{
            capability    = $Capability
            command       = $Command
            ok            = $true
            items         = $items
            truncated     = ($Enumerated -and ($items.Count -ge $script:EnumerationLimit))
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
            truncated     = $false
            errorCategory = (Get-ErrorCategory $_)
            errorId       = (Get-ErrorId $_)
        })
    }
}

# ---- Leituras (fixas, todas de verbo Get) ----------------------------------------------------------------

function Read-OrganizationConfig {
    Invoke-Read 'ExchangeOrganizationConfig' 'Get-OrganizationConfig' {
        Get-OrganizationConfig | ForEach-Object {
            @{
                auditDisabled                          = (Get-Bool $_ 'AuditDisabled')
                customerLockBoxEnabled                 = (Get-Bool $_ 'CustomerLockBoxEnabled')
                oAuth2ClientProfileEnabled             = (Get-Bool $_ 'OAuth2ClientProfileEnabled')
                mailTipsAllTipsEnabled                 = (Get-Bool $_ 'MailTipsAllTipsEnabled')
                mailTipsExternalRecipientsTipsEnabled  = (Get-Bool $_ 'MailTipsExternalRecipientsTipsEnabled')
                mailTipsGroupMetricsEnabled            = (Get-Bool $_ 'MailTipsGroupMetricsEnabled')
                mailTipsLargeAudienceThreshold         = (Get-Int $_ 'MailTipsLargeAudienceThreshold')
                bookingsEnabled                        = (Get-Bool $_ 'BookingsEnabled')
                rejectDirectSend                       = (Get-Bool $_ 'RejectDirectSend')
            }
        }
    }
}

function Read-TransportConfig {
    Invoke-Read 'ExchangeTransportConfig' 'Get-TransportConfig' {
        Get-TransportConfig | ForEach-Object {
            @{ smtpClientAuthenticationDisabled = (Get-Bool $_ 'SmtpClientAuthenticationDisabled') }
        }
    }
}

function Read-SharingPolicies {
    Invoke-Read 'ExchangeSharingPolicies' 'Get-SharingPolicy' {
        Get-SharingPolicy | ForEach-Object {
            @{
                identity  = (Get-Text $_ 'Identity')
                name      = (Get-Text $_ 'Name')
                isDefault = (Get-Bool $_ 'Default')
                enabled   = (Get-Bool $_ 'Enabled')
                domains   = (Get-List $_ 'Domains')
            }
        }
    }
}

function Read-OwaMailboxPolicies {
    Invoke-Read 'ExchangeOwaMailboxPolicies' 'Get-OwaMailboxPolicy' {
        Get-OwaMailboxPolicy | ForEach-Object {
            @{
                identity                           = (Get-Text $_ 'Identity')
                name                               = (Get-Text $_ 'Name')
                isDefault                          = (Get-Bool $_ 'IsDefault')
                additionalStorageProvidersAvailable = (Get-Bool $_ 'AdditionalStorageProvidersAvailable')
                personalAccountsEnabled            = (Get-Bool $_ 'PersonalAccountsEnabled')
                personalAccountCalendarsEnabled    = (Get-Bool $_ 'PersonalAccountCalendarsEnabled')
                bookingsMailboxCreationEnabled     = (Get-Bool $_ 'BookingsMailboxCreationEnabled')
            }
        }
    }
}

function Read-TransportRules {
    Invoke-Read 'ExchangeTransportRules' 'Get-TransportRule' {
        Get-TransportRule | ForEach-Object {
            @{
                identity                  = (Get-Text $_ 'Identity')
                name                      = (Get-Text $_ 'Name')
                state                     = (Get-Text $_ 'State')
                mode                      = (Get-Text $_ 'Mode')
                priority                  = (Get-Int $_ 'Priority')
                setScl                    = (Get-Int $_ 'SetSCL')
                senderDomainIs            = (Get-List $_ 'SenderDomainIs')
                fromAddressContainsWords  = (Get-List $_ 'FromAddressContainsWords')
                fromAddressMatchesPatterns = (Get-List $_ 'FromAddressMatchesPatterns')
                redirectMessageTo         = (Get-List $_ 'RedirectMessageTo')
                blindCopyTo               = (Get-List $_ 'BlindCopyTo')
                addToRecipients           = (Get-List $_ 'AddToRecipients')
                copyTo                    = (Get-List $_ 'CopyTo')
            }
        }
    }
}

function Read-RoleAssignmentPolicies {
    Invoke-Read 'ExchangeRoleAssignmentPolicies' 'Get-RoleAssignmentPolicy' {
        Get-RoleAssignmentPolicy | ForEach-Object {
            @{
                identity      = (Get-Text $_ 'Identity')
                name          = (Get-Text $_ 'Name')
                isDefault     = (Get-Bool $_ 'IsDefault')
                assignedRoles = (Get-List $_ 'AssignedRoles')
            }
        }
    }
}

function Read-ExternalSenderIdentification {
    Invoke-Read 'ExchangeExternalSenderIdentification' 'Get-ExternalInOutlook' {
        Get-ExternalInOutlook | ForEach-Object {
            @{
                identity  = (Get-Text $_ 'Identity')
                enabled   = (Get-Bool $_ 'Enabled')
                allowList = (Get-List $_ 'AllowList')
            }
        }
    }
}

function Read-OutboundSpamFilterPolicies {
    # A política de filtro de spam de SAÍDA não é "do Exchange puro" — ela pertence à proteção de mensagens.
    # A leitura entra aqui porque o critério de ENCAMINHAMENTO depende dela: sem o modo de encaminhamento
    # automático não é possível afirmar que todas as formas de encaminhamento estão bloqueadas. O catálogo
    # completo do Microsoft Defender para Office 365 continua fora deste bloco.
    Invoke-Read 'ExchangeOutboundSpamFilterPolicies' 'Get-HostedOutboundSpamFilterPolicy' {
        Get-HostedOutboundSpamFilterPolicy | ForEach-Object {
            @{
                identity          = (Get-Text $_ 'Identity')
                name              = (Get-Text $_ 'Name')
                isDefault         = (Get-Bool $_ 'IsDefault')
                autoForwardingMode = (Get-Text $_ 'AutoForwardingMode')
            }
        }
    }
}

function Read-Mailboxes {
    Invoke-Read 'ExchangeMailboxes' 'Get-Mailbox' {
        Get-Mailbox -ResultSize $script:EnumerationLimit -WarningAction SilentlyContinue | ForEach-Object {
            @{
                externalDirectoryObjectId  = (Get-Text $_ 'ExternalDirectoryObjectId')
                userPrincipalName          = (Get-Text $_ 'UserPrincipalName')
                displayName                = (Get-Text $_ 'DisplayName')
                recipientTypeDetails       = (Get-Text $_ 'RecipientTypeDetails')
                auditEnabled               = (Get-Bool $_ 'AuditEnabled')
                auditOwner                 = (Get-List $_ 'AuditOwner')
                auditDelegate              = (Get-List $_ 'AuditDelegate')
                auditAdmin                 = (Get-List $_ 'AuditAdmin')
                forwardingSmtpAddress      = (Get-Text $_ 'ForwardingSmtpAddress')
                forwardingAddress          = (Get-Text $_ 'ForwardingAddress')
                deliverToMailboxAndForward = (Get-Bool $_ 'DeliverToMailboxAndForward')
                roleAssignmentPolicy       = (Get-Text $_ 'RoleAssignmentPolicy')
                sharingPolicy              = (Get-Text $_ 'SharingPolicy')
            }
        }
    } $true
}

function Read-AccountSignIn {
    # Leitura SEPARADA de propósito: "caixa compartilhada" e "conta bloqueada" são dois fatos, e o critério
    # depende dos dois. Sem esta leitura, o AEGIS não converte um no outro por suposição.
    Invoke-Read 'ExchangeMailboxSignIn' 'Get-User' {
        Get-User -ResultSize $script:EnumerationLimit -WarningAction SilentlyContinue | ForEach-Object {
            @{
                externalDirectoryObjectId = (Get-Text $_ 'ExternalDirectoryObjectId')
                userPrincipalName         = (Get-Text $_ 'UserPrincipalName')
                accountDisabled           = (Get-Bool $_ 'AccountDisabled')
            }
        }
    } $true
}

function Read-CasMailboxes {
    Invoke-Read 'ExchangeCasMailboxes' 'Get-CASMailbox' {
        Get-CASMailbox -ResultSize $script:EnumerationLimit -WarningAction SilentlyContinue | ForEach-Object {
            @{
                externalDirectoryObjectId        = (Get-Text $_ 'ExternalDirectoryObjectId')
                userPrincipalName                = (Get-Text $_ 'UserPrincipalName')
                displayName                      = (Get-Text $_ 'DisplayName')
                # AUSENTE significa HERDA A ORGANIZAÇÃO. Get-Bool devolve $null nesse caso, e é assim que o
                # contrato do AEGIS o preserva — nunca como "desabilitado".
                smtpClientAuthenticationDisabled = (Get-Bool $_ 'SmtpClientAuthenticationDisabled')
                owaMailboxPolicy                 = (Get-Text $_ 'OwaMailboxPolicy')
            }
        }
    } $true
}

function Read-AuditBypassAssociations {
    Invoke-Read 'ExchangeAuditBypassAssociations' 'Get-MailboxAuditBypassAssociation' {
        Get-MailboxAuditBypassAssociation -ResultSize $script:EnumerationLimit -WarningAction SilentlyContinue | ForEach-Object {
            @{
                identity           = (Get-Text $_ 'Identity')
                name               = (Get-Text $_ 'Name')
                auditBypassEnabled = (Get-Bool $_ 'AuditBypassEnabled')
            }
        }
    } $true
}

# ---- Execução --------------------------------------------------------------------------------------------

$script:EnumerationLimit = 5000

$result = @{
    runtime                 = @{ powerShell = $null; module = $null; platform = $null }
    connected               = $false
    connectionErrorId       = $null
    connectionErrorCategory = $null
    enumerationLimit        = $script:EnumerationLimit
    reads                   = @()
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

    $limit = Get-Prop $request 'enumerationLimit'
    if ($null -ne $limit) {
        $parsed = 0
        if ([int]::TryParse(([string]$limit).Trim(), [ref]$parsed) -and $parsed -gt 0 -and $parsed -le 50000) {
            $script:EnumerationLimit = $parsed
            $result.enumerationLimit = $parsed
        }
    }

    $mode = Get-Prop $request 'mode'
    if ($mode -eq 'module-check') {
        # Validação de RUNTIME. Prova que o módulo importa e que os comandos DO MÓDULO existem nesta
        # plataforma. NÃO conecta em locatário nenhum, não precisa de credencial e — o ponto importante —
        # NÃO prova os comandos de SESSÃO: eles só são importados por uma conexão bem-sucedida, e a existência
        # deles não diria nada sobre a autorização para executá-los. Por isso eles saem numa leitura PRÓPRIA,
        # declarados como contrato, e não como verificação aprovada.
        $result.connected = $false
        foreach ($n in $script:ModuleCommands) {
            $found = $null -ne (Get-Command $n -Module ExchangeOnlineManagement -ErrorAction SilentlyContinue)
            $script:Reads.Add(@{
                capability = 'ModuleCheck'; command = $n; ok = $found; items = @(); truncated = $false
                errorCategory = $(if ($found) { $null } else { 'Unavailable' })
                errorId = $(if ($found) { $null } else { 'CommandNotFound' })
            })
        }
        $script:Reads.Add(@{
            capability = 'SessionCommandContract'; command = '(importados pela conexão)'; ok = $true
            items = @($script:SessionCommands | ForEach-Object { @{ command = $_ } })
            truncated = $false; errorCategory = $null; errorId = $null
        })
        $result.reads = $script:Reads.ToArray()
        $result | ConvertTo-Json -Depth 12 -Compress
        exit 0
    }

    $accessToken = [string](Get-Prop $request 'accessToken')
    $organization = [string](Get-Prop $request 'organization')
    if ([string]::IsNullOrWhiteSpace($accessToken)) { throw 'Token de acesso ausente na entrada.' }
    if ([string]::IsNullOrWhiteSpace($organization)) { throw 'Domínio da organização ausente na entrada.' }

    try {
        # -CommandName restringe o que a conexão importa ao conjunto FIXO que esta coleta executa: menos
        # tempo de conexão e menos superfície na sessão. Um comando que não venha por aqui falha na leitura
        # dele com "comando não encontrado" — e essa leitura é declarada, não silenciada.
        # -ShowBanner:$false e -SkipLoadingFormatData evitam saída decorativa e download de formatação: o
        # adaptador lê propriedades, não formata nada.
        Connect-ExchangeOnline `
            -AccessToken $accessToken `
            -Organization $organization `
            -CommandName $script:SessionCommands `
            -ShowBanner:$false `
            -SkipLoadingFormatData `
            -ErrorAction Stop | Out-Null
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
        Read-OrganizationConfig
        Read-TransportConfig
        Read-SharingPolicies
        Read-OwaMailboxPolicies
        Read-TransportRules
        Read-RoleAssignmentPolicies
        Read-ExternalSenderIdentification
        Read-OutboundSpamFilterPolicies
        Read-Mailboxes
        Read-AccountSignIn
        Read-CasMailboxes
        Read-AuditBypassAssociations
    }
    finally {
        # A sessão é encerrada SEMPRE: o processo é descartável, mas a conexão com o locatário não pode
        # sobreviver à coleta nem ser reaproveitada por outra.
        try { Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue | Out-Null } catch { }
    }

    $result.reads = $script:Reads.ToArray()
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
        $result.reads = $script:Reads.ToArray()
        $result | ConvertTo-Json -Depth 12 -Compress
    }
    catch {
        '{"runtime":{},"connected":false,"connectionErrorCategory":"Error","connectionErrorId":"DocumentoDeResultadoIndisponivel","reads":[]}'
    }
    exit 0
}
