#requires -Version 5.1
<#
.SYNOPSIS
    [AEGIS-AUD-048] Smoke test reproduzivel do MVP AEGIS Score (Release Candidate).

.DESCRIPTION
    Valida ponta a ponta, contra as APIs REAIS e com dados EXCLUSIVAMENTE sinteticos, o roteiro de
    demonstracao do MVP: prepara um banco DESCARTAVEL isolado (nunca toca aegis_dev), sobe a API,
    exercita liveness/readiness, cria o ambiente demo SEM segredo versionado (senha so em runtime),
    login, selecao de tenant, troca entre dois tenants, isolamento, configuracao de conectores
    genericos, ingestao SIEM/EDR, atualizacao deterministica do score, Dashboard, as seis Funcoes
    NIST, Integracoes e Hub. No fim, encerra o processo que iniciou e remove SO o banco que criou
    (validando o nome antes). Sai com codigo != 0 e mensagem clara quando um gate falha.

    Segredos (senha do PostgreSQL, chave JWT, senha demo, chaves de ingestao) sao fornecidos/gerados
    SOMENTE em runtime e NUNCA sao impressos.

    Script ASCII-only de proposito: roda igual em qualquer codepage do Windows PowerShell 5.1+.

.PARAMETER PgPassword
    Senha do papel PostgreSQL usado para criar/remover o banco descartavel e como connection string da
    app. Runtime apenas. Se ausente, usa $env:AEGIS_SMOKE_PGPASSWORD ou $env:PGPASSWORD.

.EXAMPLE
    $env:AEGIS_SMOKE_PGPASSWORD = 'postgres'
    .\scripts\smoke-mvp.ps1

.EXAMPLE
    .\scripts\smoke-mvp.ps1 -PgUser postgres -PgPassword 'senha' -ApiPort 5199
#>
[CmdletBinding()]
param(
    [int]$ApiPort = 5199,
    [string]$PgHost = 'localhost',
    [int]$PgPort = 5432,
    [string]$PgUser = 'postgres',
    [string]$PgPassword,
    [string]$PsqlPath,
    [int]$ReadyTimeoutSec = 90
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------------------------------
#  Contexto e helpers
# ---------------------------------------------------------------------------------------------------
$RepoRoot    = Split-Path -Parent $PSScriptRoot
$ApiProjDir  = Join-Path $RepoRoot 'backend\src\AegisScore.Api'
$MigratorDir = Join-Path $RepoRoot 'backend\src\AegisScore.DbMigrator'
$Base        = "http://localhost:$ApiPort"

# IDs FIXOS do seed demo (espelham DevController.DemoTenantId / DemoTenantBId).
$TenantA = 'aa000000-0000-0000-0000-000000000001'
$TenantB = 'aa000000-0000-0000-0000-000000000002'

$script:GatesPassed = 0
$script:ApiProc     = $null
$script:SmokeDb     = $null
$script:OrigEnv     = @{}
$script:Psql        = $null

function Write-Info($msg) { Write-Host "  $msg" -ForegroundColor DarkGray }
function Write-Head($msg) { Write-Host ""; Write-Host "== $msg" -ForegroundColor Cyan }

function Gate {
    param([string]$Desc, [bool]$Condition, [string]$Detail = '')
    if ($Condition) {
        $script:GatesPassed++
        Write-Host "  [PASS] $Desc" -ForegroundColor Green
    }
    else {
        $suffix = ''
        if ($Detail) { $suffix = " -> $Detail" }
        throw "[FAIL] $Desc$suffix"
    }
}

function New-Secret([int]$Bytes = 24) {
    # RandomNumberGenerator.Create().GetBytes() e portavel: existe no .NET Framework (PS 5.1) e no .NET (PS 7+).
    $b = New-Object byte[] $Bytes
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($b) } finally { $rng.Dispose() }
    return [Convert]::ToBase64String($b)
}

function Set-EnvSaved([string]$Name, [string]$Value) {
    if (-not $script:OrigEnv.ContainsKey($Name)) {
        $script:OrigEnv[$Name] = [Environment]::GetEnvironmentVariable($Name, 'Process')
    }
    Set-Item -Path "Env:$Name" -Value $Value
}

function Restore-Env {
    foreach ($k in $script:OrigEnv.Keys) {
        $v = $script:OrigEnv[$k]
        if ($null -eq $v) { Remove-Item -Path "Env:$k" -ErrorAction SilentlyContinue }
        else { Set-Item -Path "Env:$k" -Value $v }
    }
}

# HTTP compativel com PS 5.1: devolve status + conteudo mesmo em 4xx/5xx, sem lancar.
function Invoke-Api {
    param(
        [string]$Method,
        [string]$Url,
        [hashtable]$Headers,
        [string]$Body,
        [string]$ContentType = 'application/json',
        [int]$TimeoutSec = 20
    )
    $p = @{ Method = $Method; Uri = $Url; TimeoutSec = $TimeoutSec; UseBasicParsing = $true }
    if ($Headers) { $p.Headers = $Headers }
    if ($PSBoundParameters.ContainsKey('Body')) { $p.Body = $Body; $p.ContentType = $ContentType }
    try {
        $resp = Invoke-WebRequest @p
        return [pscustomobject]@{ Status = [int]$resp.StatusCode; Content = [string]$resp.Content }
    }
    catch {
        $r = $_.Exception.Response
        if ($r -and $r.StatusCode) {
            $text = ''
            try {
                $sr = New-Object System.IO.StreamReader($r.GetResponseStream())
                $text = $sr.ReadToEnd(); $sr.Close()
            } catch {}
            return [pscustomobject]@{ Status = [int]$r.StatusCode; Content = $text }
        }
        throw
    }
}

function Resolve-Psql {
    if ($PsqlPath) {
        if (Test-Path $PsqlPath) { return $PsqlPath }
        throw "psql nao encontrado no caminho informado: $PsqlPath"
    }
    $cmd = Get-Command psql -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidates = Get-ChildItem 'C:\Program Files\PostgreSQL\*\bin\psql.exe' -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending
    if ($candidates) { return $candidates[0].FullName }
    throw "psql nao localizado. Instale o cliente PostgreSQL ou passe -PsqlPath."
}

function Invoke-Psql {
    param([string]$Database, [string]$Sql)
    & $script:Psql -h $PgHost -p $PgPort -U $PgUser -w -v ON_ERROR_STOP=1 -d $Database -c $Sql 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "psql falhou (exit $LASTEXITCODE) em '$Database'." }
}

# ===================================================================================================
$exitCode = 0
try {
    Write-Head "AEGIS - Smoke MVP (Release Candidate)"

    # --- Segredos e config, SOMENTE em runtime (nunca impressos) ---
    if (-not $PgPassword) { $PgPassword = $env:AEGIS_SMOKE_PGPASSWORD }
    if (-not $PgPassword) { $PgPassword = $env:PGPASSWORD }
    if (-not $PgPassword) {
        throw "Senha do PostgreSQL ausente. Passe -PgPassword ou defina `$env:AEGIS_SMOKE_PGPASSWORD (nao versionado, nao impresso)."
    }
    $script:Psql = Resolve-Psql
    Write-Info "psql: $script:Psql"

    $jwtKey  = New-Secret 48    # >= 32 bytes p/ HS256
    $demoPw  = New-Secret 18    # senha demo de runtime (>= 8 chars)
    $siemKey = New-Secret 24    # chave de ingestao SIEM (>= 24 chars)
    $edrKey  = New-Secret 24    # chave de ingestao EDR

    # --- Banco descartavel isolado (jamais aegis_dev) ---
    $script:SmokeDb = 'aegis_smoke_' + ([guid]::NewGuid().ToString('N').Substring(0, 8))
    if ($script:SmokeDb -notmatch '^aegis_smoke_[0-9a-f]{8}$' -or $script:SmokeDb -eq 'aegis_dev') {
        throw "Nome de banco de smoke invalido: $script:SmokeDb"
    }
    $connString = "Host=$PgHost;Port=$PgPort;Database=$($script:SmokeDb);Username=$PgUser;Password=$PgPassword"

    Set-EnvSaved 'PGPASSWORD' $PgPassword

    Write-Head "1) Build do backend (API + DbMigrator)"
    & dotnet build $ApiProjDir -c Debug --nologo | Out-Null
    Gate "Build da API" ($LASTEXITCODE -eq 0)
    & dotnet build $MigratorDir -c Debug --nologo | Out-Null
    Gate "Build do DbMigrator" ($LASTEXITCODE -eq 0)

    Write-Head "2) Banco descartavel: cria e prepara pelo AegisScore.DbMigrator"
    Invoke-Psql -Database 'postgres' -Sql "CREATE DATABASE `"$($script:SmokeDb)`";"
    Write-Info "Banco criado: $script:SmokeDb"

    # A connection string vem SO de env var (o migrator/API a preferem sobre user-secrets).
    Set-EnvSaved 'ConnectionStrings__AegisScore' $connString
    Set-EnvSaved 'Jwt__SigningKey' $jwtKey
    Set-EnvSaved 'Demo__Password' $demoPw
    Set-EnvSaved 'ASPNETCORE_ENVIRONMENT' 'Development'
    Set-EnvSaved 'ASPNETCORE_URLS' $Base

    $migratorDll = Join-Path $MigratorDir 'bin\Debug\net10.0\AegisScore.DbMigrator.dll'
    & dotnet exec $migratorDll --environment Development
    Gate "DbMigrator migrate+seed+verify (exit 0)" ($LASTEXITCODE -eq 0)

    & dotnet exec $migratorDll --environment Development --verify-only
    Gate "DbMigrator --verify-only (exit 0)" ($LASTEXITCODE -eq 0)

    Write-Head "3) Sobe a API e valida liveness/readiness"
    $apiDll    = Join-Path $ApiProjDir 'bin\Debug\net10.0\AegisScore.Api.dll'
    $apiOutLog = Join-Path $env:TEMP "aegis-smoke-api-$($script:SmokeDb).out.log"
    $apiErrLog = Join-Path $env:TEMP "aegis-smoke-api-$($script:SmokeDb).err.log"
    $script:ApiProc = Start-Process -FilePath 'dotnet' -ArgumentList @('exec', $apiDll) `
        -WorkingDirectory $ApiProjDir -PassThru -NoNewWindow `
        -RedirectStandardOutput $apiOutLog -RedirectStandardError $apiErrLog

    # Aguarda readiness (com timeout) - nunca dorme indefinidamente.
    $deadline = (Get-Date).AddSeconds($ReadyTimeoutSec)
    $ready = $false
    while ((Get-Date) -lt $deadline) {
        if ($script:ApiProc.HasExited) { throw "A API encerrou antes de ficar pronta. Veja $apiErrLog" }
        try {
            $r = Invoke-Api -Method GET -Url "$Base/health/ready" -TimeoutSec 5
            if ($r.Status -eq 200 -and ($r.Content | ConvertFrom-Json).status -eq 'Healthy') { $ready = $true; break }
        } catch {}
        Start-Sleep -Milliseconds 800
    }
    Gate "Readiness ficou Healthy dentro do timeout" $ready "veja $apiErrLog"

    $live = Invoke-Api -Method GET -Url "$Base/health/live"
    Gate "Liveness 200 + Healthy" ($live.Status -eq 200 -and ($live.Content | ConvertFrom-Json).status -eq 'Healthy')

    Write-Head "4) Ambiente demo SEM segredo versionado (senha so em runtime)"
    $seedDemo = Invoke-Api -Method POST -Url "$Base/api/v1/dev/seed-demo"
    Gate "seed-demo (tenant A) 200" ($seedDemo.Status -eq 200)

    $seedUser = Invoke-Api -Method POST -Url "$Base/api/v1/dev/seed-user"
    Gate "seed-user 200 (senha via Demo__Password de runtime)" ($seedUser.Status -eq 200)
    Gate "seed-user NAO devolve a senha na resposta" ($seedUser.Content -notmatch '(?i)"password"')

    $seedB = Invoke-Api -Method POST -Url "$Base/api/v1/dev/seed-second-tenant"
    Gate "seed-second-tenant (tenant B) 200" ($seedB.Status -eq 200)

    Write-Head "5) Login, selecao de tenant e troca entre dois tenants"
    $loginBody = @{ email = 'analista@demo.example.com'; password = $demoPw } | ConvertTo-Json
    $login = Invoke-Api -Method POST -Url "$Base/api/v1/auth/login" -Body $loginBody
    Gate "login 200" ($login.Status -eq 200)
    $loginJson = $login.Content | ConvertFrom-Json
    Gate "login exige selecao (2 acessos -> selection_required)" `
        ($loginJson.status -eq 'selection_required' -and $loginJson.tenants.Count -eq 2 -and $loginJson.selectionTicket)

    $selBody = @{ selectionTicket = $loginJson.selectionTicket; targetTenantId = $TenantA } | ConvertTo-Json
    $sel = Invoke-Api -Method POST -Url "$Base/api/v1/auth/select-tenant" -Body $selBody
    Gate "select-tenant (A) 200 + access token" ($sel.Status -eq 200 -and ($sel.Content | ConvertFrom-Json).accessToken)
    $tokenA = ($sel.Content | ConvertFrom-Json).accessToken
    $authA = @{ Authorization = "Bearer $tokenA"; 'X-Tenant' = $TenantA }

    # Fail-closed: mesmo token de A, mas X-Tenant de B -> 403 (defesa cross-tenant).
    $cross = Invoke-Api -Method GET -Url "$Base/api/v1/scoring/workspace" -Headers @{ Authorization = "Bearer $tokenA"; 'X-Tenant' = $TenantB }
    Gate "X-Tenant divergente do token -> 403 (fail-closed)" ($cross.Status -eq 403)

    Write-Head "6) Conectores genericos (SIEM/EDR) + ingestao sintetica"
    $siemBody = @{ provider = 99; capability = 5; displayName = 'Demo Generic SIEM'; authType = 1
                   settings = ('{"ingestionKey":"' + $siemKey + '"}'); syncIntervalMinutes = 60 } | ConvertTo-Json
    $siemCfg = Invoke-Api -Method POST -Url "$Base/api/v1/tenants/connectors" -Headers $authA -Body $siemBody
    Gate "configura conector Generic/SIEM (201)" ($siemCfg.Status -eq 201)
    $siemId = ($siemCfg.Content | ConvertFrom-Json).id

    $edrBody = @{ provider = 99; capability = 6; displayName = 'Demo Generic EDR'; authType = 1
                  settings = ('{"ingestionKey":"' + $edrKey + '"}'); syncIntervalMinutes = 60 } | ConvertTo-Json
    $edrCfg = Invoke-Api -Method POST -Url "$Base/api/v1/tenants/connectors" -Headers $authA -Body $edrBody
    Gate "configura conector Generic/EDR (201)" ($edrCfg.Status -eq 201)
    $edrId = ($edrCfg.Content | ConvertFrom-Json).id

    $siemTest = Invoke-Api -Method POST -Url "$Base/api/v1/connectors/$siemId/test" -Headers $authA
    Gate "test do conector SIEM -> Healthy (pronto para push)" `
        ($siemTest.Status -eq 200 -and ($siemTest.Content | ConvertFrom-Json).status -eq 'Healthy')

    $siemBatch = Get-Content -Raw (Join-Path $RepoRoot 'samples\ingestion\siem-batch.example.json')
    $edrBatch  = Get-Content -Raw (Join-Path $RepoRoot 'samples\ingestion\edr-batch.example.json')

    $ingSiem = Invoke-Api -Method POST -Url "$Base/api/v1/ingestion/connectors/$siemId/events" `
        -Headers @{ 'X-Ingestion-Key' = $siemKey } -Body $siemBatch
    Gate "ingestao SIEM aceita (accepted >= 1)" ($ingSiem.Status -eq 200 -and ($ingSiem.Content | ConvertFrom-Json).accepted -ge 1)

    $ingSiemDup = Invoke-Api -Method POST -Url "$Base/api/v1/ingestion/connectors/$siemId/events" `
        -Headers @{ 'X-Ingestion-Key' = $siemKey } -Body $siemBatch
    Gate "reenvio SIEM eh deduplicado (idempotencia)" ($ingSiemDup.Status -eq 200 -and ($ingSiemDup.Content | ConvertFrom-Json).deduplicated -ge 1)

    $ingEdr = Invoke-Api -Method POST -Url "$Base/api/v1/ingestion/connectors/$edrId/events" `
        -Headers @{ 'X-Ingestion-Key' = $edrKey } -Body $edrBatch
    Gate "ingestao EDR aceita (accepted >= 1)" ($ingEdr.Status -eq 200 -and ($ingEdr.Content | ConvertFrom-Json).accepted -ge 1)

    # Chave invalida -> 401 generico (sem confirmar existencia do conector).
    $ingBad = Invoke-Api -Method POST -Url "$Base/api/v1/ingestion/connectors/$siemId/events" `
        -Headers @{ 'X-Ingestion-Key' = 'chave-obviamente-invalida-xxxxx' } -Body $siemBatch
    Gate "chave de ingestao invalida -> 401" ($ingBad.Status -eq 401)

    Write-Head "7) Score deterministico, Dashboard, seis Funcoes, Integracoes e Hub (tenant A)"
    $wsA = Invoke-Api -Method GET -Url "$Base/api/v1/scoring/workspace" -Headers $authA
    Gate "workspace (A) 200" ($wsA.Status -eq 200)
    $wsAj = $wsA.Content | ConvertFrom-Json
    Gate "score de A ficou AVALIADO apos a ingestao (deterministico)" ($wsAj.overall.evaluationState -eq 'Evaluated')
    $funcs = @($wsAj.functions | ForEach-Object { $_.code })
    $six = @('GV','ID','PR','DE','RS','RC')
    $allSix = ($six | Where-Object { $funcs -contains $_ }).Count -eq 6
    Gate "as seis Funcoes NIST presentes na projecao do workspace" $allSix ("obtidas: " + ($funcs -join ','))
    Gate "conectores de A refletidos na projecao (Configured = 2)" ($wsAj.connectors.configured -eq 2)

    $dashA = Invoke-Api -Method GET -Url "$Base/api/v1/dashboard/executive" -Headers $authA
    Gate "Dashboard executivo (A) 200" ($dashA.Status -eq 200)

    $ctrlA = Invoke-Api -Method GET -Url "$Base/api/v1/scoring/dashboard" -Headers $authA
    Gate "matriz de controles (A) 200 e nao-vazia (evidencia mapeada)" `
        ($ctrlA.Status -eq 200 -and (@(($ctrlA.Content | ConvertFrom-Json)).Count -ge 1))

    $intgA = Invoke-Api -Method GET -Url "$Base/api/v1/connectors" -Headers $authA
    Gate "Integracoes (A) lista 2 conectores com chave de ingestao" `
        ($intgA.Status -eq 200 -and (@(($intgA.Content | ConvertFrom-Json) | Where-Object { $_.hasIngestionKey }).Count -eq 2))

    $hubA = Invoke-Api -Method GET -Url "$Base/api/v1/governance/documents" -Headers $authA
    Gate "Hub documental (A) 200" ($hubA.Status -eq 200)

    Write-Head "8) Troca para o tenant B: isolamento e ausencia de fallback demo"
    $swBody = @{ targetTenantId = $TenantB } | ConvertTo-Json
    $sw = Invoke-Api -Method POST -Url "$Base/api/v1/auth/switch-tenant" -Headers @{ Authorization = "Bearer $tokenA" } -Body $swBody
    Gate "switch-tenant (A -> B) 200 + novo token" ($sw.Status -eq 200 -and ($sw.Content | ConvertFrom-Json).accessToken)
    $tokenB = ($sw.Content | ConvertFrom-Json).accessToken
    $authB = @{ Authorization = "Bearer $tokenB"; 'X-Tenant' = $TenantB }

    $wsB = Invoke-Api -Method GET -Url "$Base/api/v1/scoring/workspace" -Headers $authB
    Gate "workspace (B) 200" ($wsB.Status -eq 200)
    $wsBj = $wsB.Content | ConvertFrom-Json
    Gate "tenant B novo eh NAO AVALIADO (sem dado fabricado / fallback demo)" ($wsBj.overall.evaluationState -eq 'NotEvaluated')
    Gate "ISOLAMENTO: B nao enxerga os conectores de A (Configured = 0)" ($wsBj.connectors.configured -eq 0)

    $intgB = Invoke-Api -Method GET -Url "$Base/api/v1/connectors" -Headers $authB
    Gate "ISOLAMENTO: Integracoes de B vazias" ($intgB.Status -eq 200 -and (@(($intgB.Content | ConvertFrom-Json)).Count -eq 0))

    $dashB = Invoke-Api -Method GET -Url "$Base/api/v1/dashboard/executive" -Headers $authB
    Gate "Dashboard de B 200 (estado proprio, sem dados de A)" ($dashB.Status -eq 200)

    Write-Host ""
    Write-Host "SMOKE OK - $($script:GatesPassed) gates aprovados. Banco descartavel: $($script:SmokeDb)" -ForegroundColor Green
}
catch {
    $exitCode = 1
    Write-Host ""
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host "SMOKE FALHOU apos $($script:GatesPassed) gate(s)." -ForegroundColor Red
}
finally {
    Write-Head "Encerramento: para a API e remove o banco descartavel"

    if ($script:ApiProc -and -not $script:ApiProc.HasExited) {
        try { Stop-Process -Id $script:ApiProc.Id -Force -ErrorAction SilentlyContinue } catch {}
        Write-Info "API encerrada (PID $($script:ApiProc.Id))."
    }

    # Remove SO o banco que este script criou, validando rigorosamente o nome antes.
    if ($script:SmokeDb) {
        if ($script:SmokeDb -match '^aegis_smoke_[0-9a-f]{8}$' -and $script:SmokeDb -ne 'aegis_dev') {
            try {
                Invoke-Psql -Database 'postgres' -Sql "DROP DATABASE IF EXISTS `"$($script:SmokeDb)`" WITH (FORCE);"
                Write-Info "Banco descartavel removido: $script:SmokeDb"
            }
            catch { Write-Host "  Aviso: nao foi possivel remover $script:SmokeDb - $($_.Exception.Message)" -ForegroundColor Yellow }
        }
        else {
            Write-Host "  RECUSA de seguranca: nome '$($script:SmokeDb)' fora do padrao descartavel - nada removido." -ForegroundColor Yellow
        }
    }

    Restore-Env
}

exit $exitCode
