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

# ---- [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] Protocolo de quadros na saída padrão ------------------------------
#
# Tudo o que o AEGIS interpreta sai em linhas prefixadas por '##aegis:', escritas DIRETAMENTE na saída padrão e
# descarregadas na hora (um processo encerrado no meio ainda deixa o que já foi escrito):
#   ##aegis:stage <estágio>   — gravado ANTES de cada passo (é o que diz até onde o processo chegou);
#   ##aegis:read <json>       — UMA leitura concluída, emitida assim que termina (sobrevive a uma falha posterior);
#   ##aegis:result <json>     — o resumo final (runtime, conexão, categorias de falha).
# Qualquer outra linha (aviso de módulo, saída de host) é ignorada pelo AEGIS — não contamina o resultado.

try { [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch { }

function Write-AegisFrame {
    param([string]$Kind, [string]$Payload)
    [Console]::Out.WriteLine('##aegis:' + $Kind + ' ' + $Payload)
    [Console]::Out.Flush()
}

function Write-AegisStage {
    param([string]$Name)
    Write-AegisFrame 'stage' $Name
}

function Write-AegisMemory {
    <# Heap gerenciado deste processo, só números: em uso e comprometido. Medição não pode derrubar a coleta. #>
    try { Write-AegisFrame 'memory' ('{0} {1}' -f [GC]::GetTotalMemory($false), [GC]::GetGCMemoryInfo().TotalCommittedBytes) } catch { }
}

function Add-AegisRead {
    <#
        EMITE a leitura imediatamente como quadro próprio — e não a guarda. O quadro é a única cópia que importa: ele já saiu
        quando a próxima leitura começa, sobrevive à falha dela, e o resumo final não repete leituras.
    #>
    # [AEGIS-KNIGHT-RUNTIME-DIAGNOSIS] Antes, cada registro também ficava numa lista do script que nada lia: uma enumeração de
    # milhares de objetos permanecia no heap do processo até o fim da coleta, somada à leitura seguinte.
    param($Record)
    Write-AegisFrame 'read' ($Record | ConvertTo-Json -Depth 12 -Compress)
}

function Write-AegisResult {
    <# Resumo final. As leituras já saíram em quadros próprios: o resumo não as repete. #>
    param($Result)
    Write-AegisMemory
    Write-AegisStage 'result'
    $Result.reads = @()
    Write-AegisFrame 'result' ($Result | ConvertTo-Json -Depth 12 -Compress)
}

# ---- Contrato de comandos --------------------------------------------------------------------------------
#
# Dois conjuntos, e a distinção é o ponto: os comandos do MÓDULO existem assim que ele é importado; os
# comandos de SESSÃO só passam a existir depois de uma conexão bem-sucedida com o locatário, porque é a
# conexão que os importa. Uma verificação OFFLINE pode provar o primeiro conjunto e NÃO pode provar o segundo —
# e muito menos a autorização para executá-los. Tratar os dois como a mesma coisa produziria um gate que
# "aprova" um ambiente em que nenhuma leitura funcionaria.

# Get-EXOMailbox é comando DO MÓDULO (não vem pela conexão, não entra em -CommandName): a verificação offline prova que ele existe
# na versão instalada — e é ele que a coleta das caixas de correio executa.
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

function Invoke-Read {
    <#
        Executa UMA leitura fixa e registra o desfecho. Uma falha aqui nunca interrompe as demais.
        `$Truncated` diz se a enumeração atingiu o teto — informação que o AEGIS usa para impedir que uma
        leitura parcial aprove a organização inteira.
    #>
    param([string]$Capability, [string]$Command, [scriptblock]$Body, [bool]$Enumerated = $false)
    Write-AegisStage ('read ' + $Capability)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        if ($Enumerated) {
            # [AEGIS-KNIGHT-RUNTIME-DIAGNOSIS] Enumeração serializada ITEM A ITEM, à medida que o comando entrega cada objeto: só o
            # texto JSON acumula. Materializar milhares de projeções e serializá-las num único ConvertTo-Json era o pico de memória
            # do processo — medido com o módulo de fixture, 5000 caixas: ~150 MiB privados assim, ~52 MiB item a item, com quadro
            # semanticamente idêntico. O quadro continua UM e só sai inteiro, depois da enumeração: uma falha no meio dela descarta
            # o texto parcial e registra a falha da leitura, como antes.
            $json = [System.Text.StringBuilder]::new()
            $count = 0
            & $Body | ForEach-Object {
                if ($count -gt 0) { [void]$json.Append(',') }
                [void]$json.Append((ConvertTo-Json -InputObject $_ -Depth 12 -Compress))
                $count++
            }
            $head = @{
                capability    = $Capability
                command       = $Command
                ok            = $true
                truncated     = ($count -ge $script:EnumerationLimit)
                errorCategory = $null
                errorId       = $null
                durationMs    = [long]$sw.ElapsedMilliseconds
            } | ConvertTo-Json -Compress
            [Console]::Out.Write('##aegis:read ' + $head.Substring(0, $head.Length - 1) + ',"items":[')
            [Console]::Out.Write($json)
            [Console]::Out.WriteLine(']}')
            [Console]::Out.Flush()
            return
        }
        $produced = & $Body
        # `@()` sobre uma List[object] com hashtables lança "Argument types do not match" no PowerShell 7.6.5.
        # Materializar com .ToArray()/cast explícito é o caminho que funciona nas duas famílias de versão.
        # [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] Atribuição em cada ramo: a saída de um `if` passa pelo pipeline e DESEMBRULHA o array de
        # um item (`, $produced` virava o objeto) — leituras de objeto único saíam como objeto e o AEGIS as lia como lista vazia.
        if ($null -eq $produced) { $items = @() } elseif ($produced -is [System.Array]) { $items = $produced } else { $items = , $produced }
        Add-AegisRead @{
            capability    = $Capability
            command       = $Command
            ok            = $true
            items         = $items
            truncated     = ($Enumerated -and ($items.Count -ge $script:EnumerationLimit))
            errorCategory = $null
            errorId       = $null
            durationMs    = [long]$sw.ElapsedMilliseconds
        }
    }
    catch {
        Add-AegisRead @{
            capability    = $Capability
            command       = $Command
            ok            = $false
            items         = @()
            truncated     = $false
            errorCategory = (Get-ErrorCategory $_)
            errorId       = (Get-ErrorId $_)
            durationMs    = [long]$sw.ElapsedMilliseconds
        }
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

# [AEGIS-KNIGHT-RUNTIME-DIAGNOSIS] Caixas de correio por Get-EXOMailbox, pedindo SÓ as propriedades que o KNIGHT usa. Get-Mailbox
# devolvia o objeto inteiro de cada caixa (centenas de propriedades) para depois aproveitar treze — numa enumeração de milhares de
# caixas, esse objeto é o que pesava no heap do processo. Todas as treze estão documentadas nos conjuntos de Get-EXOMailbox
# (Minimum, Audit, Delivery e Policy). A lista é ÚNICA: é ela que vai em -Properties e é ela que a projeção confere em cada objeto.
# Uma propriedade pedida que não volta no objeto NÃO é tratada como nula, falsa ou lista vazia (isso aprovaria encaminhamento ou
# auditoria por omissão): a leitura falha com erro de CONTRATO e os controles dela ficam não avaliados.
# Get-CASMailbox e Get-User continuam como estão: SmtpClientAuthenticationDisabled e OwaMailboxPolicy não constam em nenhum conjunto
# documentado de Get-EXOCASMailbox, e AccountDisabled não tem equivalente nos cmdlets Get-EXO*.
$script:MailboxProperties = @(
    'ExternalDirectoryObjectId', 'UserPrincipalName', 'DisplayName', 'RecipientTypeDetails',
    'AuditEnabled', 'AuditOwner', 'AuditDelegate', 'AuditAdmin',
    'ForwardingSmtpAddress', 'ForwardingAddress', 'DeliverToMailboxAndForward',
    'RoleAssignmentPolicy', 'SharingPolicy'
)

function Assert-AegisProjection {
    <# Toda propriedade pedida precisa EXISTIR no objeto devolvido (o valor pode ser nulo — ausência é outra coisa). #>
    param($Object, [string[]]$Names)
    foreach ($n in $Names) {
        if ($null -eq $Object -or $null -eq $Object.PSObject.Properties[$n]) {
            throw [System.InvalidOperationException]::new('AegisContractPropertyMissing.' + $n)
        }
    }
}

function Read-Mailboxes {
    Invoke-Read 'ExchangeMailboxes' 'Get-EXOMailbox' {
        Get-EXOMailbox -Properties $script:MailboxProperties -ResultSize $script:EnumerationLimit -WarningAction SilentlyContinue | ForEach-Object {
            Assert-AegisProjection $_ $script:MailboxProperties
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
    Write-AegisStage 'start'
    $result.runtime.powerShell = [string]$PSVersionTable.PSVersion
    $result.runtime.platform = [string]$PSVersionTable.Platform

    Write-AegisStage 'input'
    $raw = [Console]::In.ReadToEnd()
    if ([string]::IsNullOrWhiteSpace($raw)) { throw 'Nenhuma credencial recebida na entrada padrão.' }
    $request = $raw | ConvertFrom-Json

    Write-AegisStage 'import'
    Import-Module ExchangeOnlineManagement -ErrorAction Stop
    Write-AegisStage 'imported'
    Write-AegisMemory
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
            Add-AegisRead @{
                capability = 'ModuleCheck'; command = $n; ok = $found; items = @(); truncated = $false
                errorCategory = $(if ($found) { $null } else { 'Unavailable' })
                errorId = $(if ($found) { $null } else { 'CommandNotFound' })
            }
        }
        Add-AegisRead @{
            capability = 'SessionCommandContract'; command = '(importados pela conexão)'; ok = $true
            items = @($script:SessionCommands | ForEach-Object { @{ command = $_ } })
            truncated = $false; errorCategory = $null; errorId = $null
        }
        Write-AegisResult $result
        exit 0
    }

    $accessToken = [string](Get-Prop $request 'accessToken')
    $organization = [string](Get-Prop $request 'organization')
    if ([string]::IsNullOrWhiteSpace($accessToken)) { throw 'Token de acesso ausente na entrada.' }
    if ([string]::IsNullOrWhiteSpace($organization)) { throw 'Domínio da organização ausente na entrada.' }

    # [AEGIS-KNIGHT-ACCESS-01] `test`: a MESMA conexão de aplicativo do modo `collect`, mas restrita a UM
    # comando representativo (Get-OrganizationConfig) em vez das doze capacidades. É a verificação que a tela
    # de Integrações usa para dar retorno rápido — conecta de verdade e lê de verdade, sem esperar pela
    # enumeração de caixas de correio nem persistir avaliação nenhuma. Não substitui uma sincronização.
    $testOnly = ($mode -eq 'test')
    $commandsToImport = if ($testOnly) { @('Get-OrganizationConfig') } else { $script:SessionCommands }

    try {
        # -CommandName restringe o que a conexão importa ao conjunto FIXO que esta coleta executa: menos
        # tempo de conexão e menos superfície na sessão. Um comando que não venha por aqui falha na leitura
        # dele com "comando não encontrado" — e essa leitura é declarada, não silenciada.
        # -ShowBanner:$false e -SkipLoadingFormatData evitam saída decorativa e download de formatação: o
        # adaptador lê propriedades, não formata nada.
        $connect = @{
            AccessToken           = $accessToken
            Organization          = $organization
            CommandName           = $commandsToImport
            ShowBanner            = $false
            SkipLoadingFormatData = $true
            ErrorAction           = 'Stop'
        }
        # [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] Módulo temporário da conexão no diretório PRIVADO desta execução
        # (-EXOModuleBasePath, documentado a partir da 3.9.2), quando a versão instalada o aceita.
        $workDir = [string](Get-Prop $request 'workDir')
        if (-not [string]::IsNullOrWhiteSpace($workDir) -and (Test-Path -LiteralPath $workDir) -and
            (Get-Command Connect-ExchangeOnline).Parameters.ContainsKey('EXOModuleBasePath')) {
            $connect['EXOModuleBasePath'] = $workDir
        }
        Write-AegisStage 'connect'
        Connect-ExchangeOnline @connect | Out-Null
        $result.connected = $true
        Write-AegisStage 'connected'
    }
    catch {
        $result.connected = $false
        $result.connectionErrorCategory = Get-ErrorCategory $_
        $result.connectionErrorId = Get-ErrorId $_
        Write-AegisStage 'connect-failed'
        Write-AegisResult $result
        exit 0
    }

    try {
        if ($testOnly) {
            Read-OrganizationConfig
        }
        else {
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
    }
    finally {
        # A sessão é encerrada SEMPRE: o processo é descartável, mas a conexão com o locatário não pode
        # sobreviver à coleta nem ser reaproveitada por outra.
        Write-AegisStage 'disconnect'
        try { Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue | Out-Null } catch { }
    }

    Write-AegisResult $result
    exit 0
}
catch {
    # ÚLTIMA LINHA DE DEFESA. Este bloco roda quando algo já deu errado, e ele próprio pode falhar: sob
    # Set-StrictMode, uma propriedade ausente num objeto de erro inesperado derrubaria o script aqui dentro. Se
    # isso acontecesse, o processo terminaria SEM NENHUMA SAÍDA — e o AEGIS veria apenas "terminou com código 1",
    # sem categoria nem motivo. Por isso a montagem do documento é protegida e, no pior caso, um documento
    # MÍNIMO é escrito à mão: o contrato de saída é sempre honrado.
    $registro = $_
    # [AEGIS-KNIGHT-COLLECTION-RELIABILITY-01] Falha DEPOIS da conexão não é falha de conexão: as leituras já emitidas valem,
    # e o processo sai com código 1 para o AEGIS não declarar a coleta íntegra. Antes da conexão, a falha é classificada
    # (o estágio declarado diz onde ela aconteceu) e o processo sai com 0, como sempre.
    $depoisDaConexao = $false
    try { $depoisDaConexao = [bool]$result.connected } catch { }
    try {
        if (-not $depoisDaConexao) {
            $result.connected = $false
            $result.connectionErrorCategory = Get-ErrorCategory $registro
            $result.connectionErrorId = Get-ErrorId $registro
        }
        Write-AegisResult $result
    }
    catch {
        [Console]::Out.WriteLine('##aegis:result {"runtime":{},"connected":false,"connectionErrorCategory":"Error","connectionErrorId":"DocumentoDeResultadoIndisponivel","reads":[]}')
        [Console]::Out.Flush()
    }
    if ($depoisDaConexao) { exit 1 }
    exit 0
}
