# AEGIS — Guia de setup em máquina nova

Runbook para colocar o AEGIS (backend .NET 10 + frontend Angular 19 + PostgreSQL) rodando
do zero num notebook novo, **sem alterar o código**. Segue exatamente a fundação atual do
repositório; nenhum passo aqui exige modificar `Program.cs`, `AuthService`, `environment.ts`
ou qualquer arquivo versionado.

> Por que este guia existe: os segredos (chave JWT e credenciais do banco) ficam **fora do git**
> de propósito (commit `6f96287`). Numa máquina nova eles não vêm pelo `git pull` — precisam ser
> configurados uma vez. É a causa raiz mais comum de _"Credenciais inválidas"_ e de falha de boot.

---

## Topologia local

| Componente | Endereço | Origem no código |
|---|---|---|
| API (.NET) | `http://localhost:5100` | `Properties/launchSettings.json` (perfil `http`) |
| Frontend (Angular) | `http://localhost:5173` | `frontend/angular.json` → `serve.port` (liberado no CORS) |
| PostgreSQL | `localhost:5432`, banco `aegis` | connection string em user-secrets/env (`ConnectionStrings:AegisScore`) |
| Tenant demo A (fixo) | `aa000000-0000-0000-0000-000000000001` | `DevController.DemoTenantId` (o tenant ativo vem do claim `tenant_id` do JWT) |
| Tenant demo B (fixo) | `aa000000-0000-0000-0000-000000000002` | `DevController.DemoTenantBId` (2º ambiente p/ seleção/troca/isolamento) |
| Usuário demo | `analista@demo.example.com` / senha **de runtime** (`Demo:Password`) | `POST /api/v1/dev/seed-user` |

---

## Pré-requisitos

- **.NET SDK 10** (`dotnet --version` deve reportar 10.x)
- **PostgreSQL 14+** rodando e acessível em `localhost:5432`
- **Node.js 18+** e npm (para o frontend Angular 19)

> Os comandos abaixo estão em **PowerShell** (Windows). Onde a sintaxe difere, segue o equivalente em
> **bash/curl** (Linux/macOS). `psql`, `dotnet` e `npm` são idênticos nos dois ambientes.

---

## Passo 1 — PostgreSQL: criar role e banco

O role de login e o banco precisam existir antes de preparar o schema. As migrações **não** são
aplicadas no boot da API: quem migra e semeia é o `AegisScore.DbMigrator` (Passo 3). Não rode
`dotnet ef database update` à mão. Via `psql` (como superusuário `postgres`):

```sql
CREATE ROLE aegis WITH LOGIN PASSWORD 'aegis';
CREATE DATABASE aegis OWNER aegis;
```

> Pode usar outro usuário/senha/porta — só reflita a escolha na connection string do Passo 2.

---

## Passo 2 — Segredos do backend (uma vez por máquina)

O `appsettings.json` versionado deixa `Jwt:SigningKey` e a connection string **vazios de
propósito**. Preencha-os via `dotnet user-secrets` (o `.csproj` já tem `UserSecretsId`, e o
`WebApplication.CreateBuilder` carrega esses segredos automaticamente em Development):

```powershell
cd C:\Projetos\AEGIS\backend\src\AegisScore.Api

# Chave JWT — precisa de >= 32 bytes (HS256), senão o boot ABORTA em Program.cs.
# Gera uma chave forte aleatória:
$bytes = New-Object byte[] 48
[Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
$key = [Convert]::ToBase64String($bytes)
dotnet user-secrets set "Jwt:SigningKey" $key

# Connection string (ajuste usuário/senha/porta se mudou no Passo 1):
dotnet user-secrets set "ConnectionStrings:AegisScore" "Host=localhost;Port=5432;Database=aegis;Username=aegis;Password=aegis"

# [AEGIS-AUD-048] Senha do usuário demo — fornecida SÓ em runtime (nunca versionada). Sem ela, o
# seed-user recusa. Escolha uma senha forte própria (>= 8 chars); ela NÃO é devolvida pela API nem logada.
dotnet user-secrets set "Demo:Password" "<escolha-uma-senha-demo-forte>"
```

> A `Demo:Password` também pode vir por variável de ambiente `Demo__Password` (útil no smoke e em CI),
> em vez de user-secrets. Restrita a `DEBUG` + ambiente `Development`; não existe porta de acesso demo em produção.

_Equivalente em bash (Linux/macOS, requer `openssl`):_

```bash
cd ~/Projetos/AEGIS/backend/src/AegisScore.Api   # ajuste o caminho conforme a máquina

key=$(openssl rand -base64 48)                    # chave JWT forte (>= 32 bytes)
dotnet user-secrets set "Jwt:SigningKey" "$key"

dotnet user-secrets set "ConnectionStrings:AegisScore" "Host=localhost;Port=5432;Database=aegis;Username=aegis;Password=aegis"
```

Conferir o que ficou guardado:

```powershell
dotnet user-secrets list
```

> ⚠️ **Nunca** comite esses valores no `appsettings.json`. Manter `Username=;Password=` e
> `SigningKey: ""` vazios no arquivo versionado é intencional — segredo em user-secrets, não no git.

---

## Passo 3 — Preparar o banco (DbMigrator) e subir a API

Primeiro prepare o banco com o **`AegisScore.DbMigrator`** — é o ÚNICO processo que aplica migrations e
semeia o catálogo/regras (a API não faz mais isso no boot). Em Development ele lê a **mesma** connection
string do Passo 2 (compartilha o `UserSecretsId` da API):

```powershell
dotnet run --project C:\Projetos\AEGIS\backend\src\AegisScore.DbMigrator -- --environment Development
```

Opções reais (de `MigratorOptions`):

- `--verify-only` — apenas verifica o estado do banco; não migra nem semeia;
- `--skip-seed` — aplica migrations e verifica, sem semear catálogo e regras;
- `--environment, -e <nome>` — ambiente de configuração (padrão: `DOTNET_ENVIRONMENT` ou `Production`).

> Não existe `--connection`: a connection string vem só de user-secrets/env, nunca por argumento.
> Códigos de saída: `0` ok · `1` config inválida · `2` migration · `3` seed · `4` verificação ·
> `5` banco inacessível · `6` advisory lock não adquirido.

Depois suba a API:

```powershell
cd C:\Projetos\AEGIS\backend\src\AegisScore.Api
dotnet run
```

Use **`dotnet run`**, não `dotnet exec ...dll` nem `dotnet bin\...\AegisScore.Api.dll`:

- `dotnet run` lê o `launchSettings.json` → define `ASPNETCORE_ENVIRONMENT=Development` → carrega
  os user-secrets do Passo 2 e sobe na porta 5100.
- `dotnet exec`/rodar a DLL direto **ignora** o `launchSettings.json` → sobe em **Production** →
  user-secrets não carregam → `Jwt:SigningKey` vazio → boot aborta com _"Jwt:SigningKey ausente ou fraca"_.

No boot a API **apenas verifica** a prontidão do schema (`SchemaReadinessGuard`) — não aplica migrations
nem semeia. Se o banco não estiver preparado pelo DbMigrator, o boot **aborta** pedindo exatamente isso.
Com o banco pronto, espere ver:

```
Startup: banco verificado — migrations aplicadas nos dois contextos, catálogo NIST CSF 2.0 e regras de avaliação presentes e íntegros.
```

Swagger disponível em `http://localhost:5100/swagger`.

---

## Passo 4 — Semear tenant e usuário demo

Os utilitários de seed vivem no `DevController`, que é compilado **apenas em DEBUG** (`#if DEBUG`)
e só responde em Development. O `dotnet run` padrão é Debug, então estão disponíveis. (Um
`dotnet run -c Release` remove esses endpoints — não use Release para o fluxo de dev.)

Com a API no ar, num outro terminal:

```powershell
# Dados do dashboard (tenant demo A, unidades, ativos, riscos...). Idempotente.
Invoke-RestMethod -Method Post -Uri http://localhost:5100/api/v1/dev/seed-demo

# Usuário logável (usa Demo:Password do Passo 2). Idempotente. A senha NÃO é devolvida.
Invoke-RestMethod -Method Post -Uri http://localhost:5100/api/v1/dev/seed-user

# 2º tenant demo + acesso do mesmo usuário (exercita seleção/troca/isolamento). Idempotente.
Invoke-RestMethod -Method Post -Uri http://localhost:5100/api/v1/dev/seed-second-tenant
```

_Equivalente com curl:_

```bash
curl -X POST http://localhost:5100/api/v1/dev/seed-demo
curl -X POST http://localhost:5100/api/v1/dev/seed-user
curl -X POST http://localhost:5100/api/v1/dev/seed-second-tenant
```

O `seed-user` cria `analista@demo.example.com` no `DemoTenantId`, com a senha definida em `Demo:Password`
(runtime). Não precisa do header `X-Tenant` — grava sob o `SystemTenantContext` do tenant demo. Com acesso
aos **dois** ambientes, o login passa a exigir **seleção explícita** de tenant.

---

## Passo 5 — Subir o frontend e logar

```powershell
cd C:\Projetos\AEGIS\frontend
npm install      # só na primeira vez
npm start        # ng serve → http://localhost:5173
```

O `environment.ts` já aponta `apiBase` para `http://localhost:5100` — **não precisa mexer nele**. O
tenant ativo **não** é mais configurado no frontend: vem do claim `tenant_id` do próprio JWT, e o
interceptor deriva dele o header `X-Tenant`. Faça login com:

- **E-mail:** `analista@demo.example.com`
- **Senha:** a que você definiu em `Demo:Password` (Passo 2)

Como o usuário demo tem acesso aos **dois** tenants, o login apresenta a tela de **seleção de ambiente** —
escolha "Grupo Aegis (Demo)". O seletor de tenant no topo troca para "Aegis Secundário (Demo)" sem vazar
estado do ambiente anterior.

---

## Passo 6 — Health checks (liveness/readiness)

[AEGIS-AUD-048] Dois endpoints **anônimos** e de resposta sanitizada (só o status; nunca connection
string, stack trace ou detalhe interno):

- **Liveness** — `GET /health/live`: confirma só que o **processo** está vivo. Não toca banco, IA nem
  fornecedor externo. É o que um orquestrador usa para decidir reiniciar um processo travado.
- **Readiness** — `GET /health/ready`: confirma que a app está **apta a servir** — PostgreSQL acessível
  + migrations dos dois contextos + catálogo/regras íntegros (mesmo `SchemaReadinessGuard` do boot).
  A ausência de Azure OpenAI / Entra / conectores **não** reprova.

```powershell
Invoke-RestMethod http://localhost:5100/health/live    # { status = Healthy }
Invoke-RestMethod http://localhost:5100/health/ready   # { status = Healthy, checks = [...] }
```

`Healthy` → HTTP 200; indisponível → HTTP 503 com o mesmo corpo mínimo.

---

## Passo 7 — Smoke test do MVP

[AEGIS-AUD-048] `scripts/smoke-mvp.ps1` valida o roteiro inteiro contra as APIs reais, com dados
**exclusivamente sintéticos**, num **banco descartável isolado** (`aegis_smoke_<hex>`) — nunca toca
`aegis_dev`. Prepara o banco pelo `DbMigrator`, sobe a API, exercita liveness/readiness, cria o ambiente
demo (senha só em runtime), login, seleção, troca entre dois tenants, isolamento, conectores genéricos,
ingestão SIEM/EDR, score determinístico, Dashboard, as seis Funções, Integrações e Hub. No fim, encerra
a API e remove **só** o banco que criou (validando o nome antes). Sai `!= 0` com mensagem clara se um gate
falha; segredos vêm só de runtime e nunca são impressos.

O papel PostgreSQL usado precisa poder `CREATE DATABASE` (o `stars`/`postgres` locais têm). Forneça a
senha por variável de ambiente (nunca versionada):

```powershell
$env:AEGIS_SMOKE_PGPASSWORD = '<senha-do-postgres>'
.\scripts\smoke-mvp.ps1 -PgUser stars
$env:AEGIS_SMOKE_PGPASSWORD = $null
```

Parâmetros úteis: `-ApiPort` (padrão 5199, não colide com o dev na 5100), `-PgUser`, `-PgPassword`,
`-PsqlPath`, `-ReadyTimeoutSec`. Roda em Windows PowerShell 5.1+ (script ASCII-only).

---

## Roteiro de demonstração (~5 min)

1. Prepare o banco (Passo 3) e suba **API** (5100) e **frontend** (5173). Garanta `Demo:Password` (Passo 2).
2. Seed: `seed-demo`, `seed-user`, `seed-second-tenant` (Passo 4).
3. **Login** em `http://localhost:5173` com o usuário demo → **selecione** "Grupo Aegis (Demo)".
4. **Dashboard**: AEGIS Score + cobertura (note "não avaliado" ≠ 0%), maturidade/ICR, saúde honesta dos
   conectores.
5. **Integrações**: configure um **Generic SIEM** e um **Generic EDR** (defina uma chave de ingestão).
6. **Ingestão** (outro terminal): use `samples/ingestion/*.example.json` com o header `X-Ingestion-Key`
   (ver `samples/ingestion/README.md`). O score do tenant se atualiza deterministicamente.
7. Percorra as **seis Funções** (GV/ID/PR/DE/RS/RC), o **Hub** (Govern) e o **Inventário** (Identify).
8. **Troque** para "Aegis Secundário (Demo)" pelo seletor de tenant → tudo zera (isolamento; sem fallback demo).

---

## Limitações conhecidas do MVP

- **Federação Entra ID real** exige App Registration/consentimento externos (fora do repo). O modo padrão
  é login local; a demonstração usa dados sintéticos.
- **Adaptadores de fabricante** (Sentinel, CrowdStrike, Splunk, Google SecOps, AWS) estão honestamente
  marcados como **não implementados** — o caminho suportado é o **contrato genérico autenticado** (push).
- Sem observabilidade distribuída, HA, DR, hardening completo ou suíte ampla de frontend (pós-MVP).
- O `DevController` (seeds) só existe em **DEBUG + Development**; não há porta de acesso demo em produção.

---

## Solução de problemas — _"Credenciais inválidas"_ (401)

O login filtra o usuário pelo tenant ambiente (`X-Tenant`) e valida a senha com PBKDF2. Qualquer
um destes pontos produz o **mesmo** 401 genérico (proposital: não vaza se o e-mail existe). Cheque
na ordem:

| Sintoma / causa | Verificação | Correção |
|---|---|---|
| Banco recém-criado, `seed-user` não rodou | Não há usuário no tenant | Rode o Passo 4 |
| `X-Tenant` diverge do tenant do usuário | JWT de outro tenant ativo | O `X-Tenant` é derivado do claim `tenant_id` do JWT; refaça login no tenant demo |
| API subiu em Production (via DLL/`exec`) | Boot reclamou de `Jwt:SigningKey`, ou `/swagger` não abre | Suba com `dotnet run` (Passo 3) |
| Hash de senha em formato incompatível | Usuário criado por outro hasher que não o `Pbkdf2PasswordHasher` | Recrie via `seed-user`; não introduza `Identity.PasswordHasher` |
| Endpoints de seed retornam 404 | API rodando em Release ou fora de Development | Use `dotnet run` (Debug + Development) |

Outras falhas comuns:

- **Boot aborta com _"Jwt:SigningKey ausente ou fraca"_** → user-secrets não configurados nesta
  máquina (Passo 2) ou API subiu em Production (Passo 3).
- **Erro de conexão com o banco no boot** → PostgreSQL parado, ou role/banco do Passo 1 ausentes,
  ou credenciais da connection string erradas.
- **CORS bloqueia o front** → o dev server precisa estar em `http://localhost:5173` (config padrão
  do `angular.json`); o CORS da API libera 5173/5273/3000, não o 4200.

---

## Regra de ouro

Segredos ficam em **user-secrets** (backend) — nunca no `appsettings.json` versionado. Tudo o mais
(chave JWT forte, tenant demo, usuário demo) é reproduzível pelos passos acima, em qualquer máquina,
sem tocar na fundação do código.
