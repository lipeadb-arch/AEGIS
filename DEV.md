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
| PostgreSQL | `localhost:5432`, banco `aegis` | `appsettings.json` → `ConnectionStrings:AegisScore` |
| Tenant demo (fixo) | `aa000000-0000-0000-0000-000000000001` | `DevController.DemoTenantId` = `environment.tenantId` |
| Usuário demo | `analista@demo.aegis` / `Aegis@12345` | `POST /api/v1/dev/seed-user` |

---

## Pré-requisitos

- **.NET SDK 10** (`dotnet --version` deve reportar 10.x)
- **PostgreSQL 14+** rodando e acessível em `localhost:5432`
- **Node.js 18+** e npm (para o frontend Angular 19)

> Os comandos abaixo estão em **PowerShell** (Windows). Onde a sintaxe difere, segue o equivalente em
> **bash/curl** (Linux/macOS). `psql`, `dotnet` e `npm` são idênticos nos dois ambientes.

---

## Passo 1 — PostgreSQL: criar role e banco

O schema é criado automaticamente pelas migrações no boot da API (`db.Database.MigrateAsync()`
em `Program.cs`) — **não** rode `dotnet ef database update` à mão. Mas o role de login e o banco
precisam existir antes. Via `psql` (como superusuário `postgres`):

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
```

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

## Passo 3 — Subir a API

```powershell
cd C:\Projetos\AEGIS\backend\src\AegisScore.Api
dotnet run
```

Use **`dotnet run`**, não `dotnet exec ...dll` nem `dotnet bin\...\AegisScore.Api.dll`:

- `dotnet run` lê o `launchSettings.json` → define `ASPNETCORE_ENVIRONMENT=Development` → carrega
  os user-secrets do Passo 2 e sobe na porta 5100.
- `dotnet exec`/rodar a DLL direto **ignora** o `launchSettings.json` → sobe em **Production** →
  user-secrets não carregam → `Jwt:SigningKey` vazio → boot aborta com _"Jwt:SigningKey ausente ou fraca"_.

No boot, a API aplica as migrações e semeia o catálogo NIST CSF 2.0 automaticamente. Espere ver:

```
Startup: migrações aplicadas e catálogo NIST CSF 2.0 verificado/semeado.
```

Swagger disponível em `http://localhost:5100/swagger`.

---

## Passo 4 — Semear tenant e usuário demo

Os utilitários de seed vivem no `DevController`, que é compilado **apenas em DEBUG** (`#if DEBUG`)
e só responde em Development. O `dotnet run` padrão é Debug, então estão disponíveis. (Um
`dotnet run -c Release` remove esses endpoints — não use Release para o fluxo de dev.)

Com a API no ar, num outro terminal:

```powershell
# Dados do dashboard (tenant demo, unidades, ativos, riscos...). Idempotente.
Invoke-RestMethod -Method Post -Uri http://localhost:5100/api/v1/dev/seed-demo

# Usuário logável no tenant demo. Idempotente.
Invoke-RestMethod -Method Post -Uri http://localhost:5100/api/v1/dev/seed-user
```

_Equivalente com curl:_

```bash
curl -X POST http://localhost:5100/api/v1/dev/seed-demo
curl -X POST http://localhost:5100/api/v1/dev/seed-user
```

O `seed-user` cria `analista@demo.aegis` / `Aegis@12345` no `DemoTenantId`. Ele **não** precisa do
header `X-Tenant` — grava sob o `SystemTenantContext` do tenant demo.

---

## Passo 5 — Subir o frontend e logar

```powershell
cd C:\Projetos\AEGIS\frontend
npm install      # só na primeira vez
npm start        # ng serve → http://localhost:5173
```

O `environment.ts` já aponta `apiBase` para `http://localhost:5100` e `tenantId` para o
`aa000000-…0001` (o mesmo `DemoTenantId` que o seed usa) — **não precisa mexer nele**. O
interceptor envia esse GUID no header `X-Tenant`. Faça login com:

- **E-mail:** `analista@demo.aegis`
- **Senha:** `Aegis@12345`

---

## Solução de problemas — _"Credenciais inválidas"_ (401)

O login filtra o usuário pelo tenant ambiente (`X-Tenant`) e valida a senha com PBKDF2. Qualquer
um destes pontos produz o **mesmo** 401 genérico (proposital: não vaza se o e-mail existe). Cheque
na ordem:

| Sintoma / causa | Verificação | Correção |
|---|---|---|
| Banco recém-criado, `seed-user` não rodou | Não há usuário no tenant | Rode o Passo 4 |
| `X-Tenant` diverge do tenant do usuário | `environment.tenantId` ≠ `DemoTenantId` | Mantenha `aa000000-…0001` no `environment.ts` (é o valor correto) |
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
