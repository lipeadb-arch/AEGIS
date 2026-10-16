#requires -Version 7.0
<#
.SYNOPSIS
    Valida a API local da plataforma Aegis Score (KNIGHT e NIST).
.DESCRIPTION
    Usa uma API ja iniciada. Nao cria banco, nao coleta em fornecedores e nao publica registros.
    Informe um JWT do ambiente para conferir NIST, ativos e documentos autenticados.
#>
[CmdletBinding()]
param(
    [string]$Base = 'http://localhost:5100',
    [string]$AccessToken = $env:AEGIS_SMOKE_ACCESS_TOKEN
)
$ErrorActionPreference = 'Stop'
$Base = $Base.TrimEnd('/')
function Assert-Status([string]$Path, [int]$Expected, [hashtable]$Headers = @{}) {
    $response = Invoke-WebRequest -Uri ($Base + $Path) -Headers $Headers -SkipHttpErrorCheck
    if ([int]$response.StatusCode -ne $Expected) {
        throw ('Falha em ' + $Path + ': esperado ' + $Expected + ', recebido ' + $response.StatusCode)
    }
    Write-Output ('PASS ' + $Path)
}
Assert-Status '/health/live' 200
Assert-Status '/health/ready' 200
Assert-Status '/' 200
if ([string]::IsNullOrWhiteSpace($AccessToken)) {
    throw 'Informe AEGIS_SMOKE_ACCESS_TOKEN ou -AccessToken para validar as jornadas autenticadas.'
}
$headers = @{ Authorization = 'Bearer ' + $AccessToken }
Assert-Status '/api/v1/nist/assessments' 200 $headers
Assert-Status '/api/v1/assets' 200 $headers
Assert-Status '/api/v1/governance/documents' 200 $headers
Assert-Status '/api/v1/scoring/current' 410 $headers
Assert-Status '/api/v1/vulnerabilities' 410 $headers
Write-Output 'PASS plataforma KNIGHT/NIST, biblioteca, inventario e aposentadoria dos endpoints antigos'
