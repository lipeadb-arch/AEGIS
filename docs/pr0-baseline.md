# AEGIS — PR 0: Linha de Base Técnica Local

> Documento **não funcional** e **aditivo**. Estabelece uma referência reproduzível do estado
> técnico do repositório **antes** das remediações do Plano Diretor, para que PRs futuros consigam
> distinguir falhas preexistentes, problemas de ambiente e regressões novas.
>
> Este documento **não corrige** nenhum achado `AEGIS-AUD-*`. Ele apenas registra e referencia.

## 1. Metadados da validação

| Campo | Valor |
|---|---|
| Commit-base (HEAD) | `1efc80ef93f745726a9dc19e04b5347fb42733d6` |
| Branch base | `main` (branch de trabalho deste PR: `chore/pr0-baseline`) |
| Upstream | `origin/main` — em sincronia (divergência `0 / 0`) |
| Remote | `https://github.com/lipeadb-arch/AEGIS.git` (sem credenciais embutidas) |
| Data da validação | **2026-07-20** |
| Sistema de validação | Windows 10 (10.0.19045); PowerShell + Git Bash |
| Última sincronização remota | `git fetch --prune origin` em 2026-07-20 (podou o ref obsoleto `origin/claude/brave-newton-rovcge`; `origin/main` inalterado) |

**Estado do working tree na validação:** limpo (0 staged / 0 unstaged / 0 untracked). Nenhuma
operação Git em andamento (sem merge/rebase/cherry-pick). `bin/`, `obj/`, `dist/`, `node_modules/`
e `.env` são ignorados por `.gitignore`.

## 2. Estrutura resumida do repositório

```
AEGIS/
├─ global.json                 # pin do SDK .NET: 10.0.100 (rollForward latestMinor)
├─ README.md / DEV.md / ARCHITECTURE.md / AEGIS_STATE.md   # documentação (ver drift em §12)
├─ backend/
│  ├─ AegisScore.sln
│  ├─ src/
│  │  ├─ AegisScore.Domain/                 # entidades, enums, VOs, regras puras
│  │  ├─ AegisScore.Application/            # interfaces (IA/conector/tenant), scoring, blast radius
│  │  ├─ AegisScore.Infrastructure/         # EF Core (PostgreSQL), Auth, IA, connectors, Migrations/ (17)
│  │  ├─ AegisScore.Connectors.Microsoft/   # adapters de exemplo (stubs Entra ID / SharePoint)
│  │  └─ AegisScore.Api/                    # ASP.NET Core: Program.cs, controllers, DTOs, Data/ (catálogo NIST)
│  └─ tests/
│     └─ AegisScore.Infrastructure.Tests/   # ÚNICO projeto de testes (xUnit + SQLite in-memory)
└─ frontend/
   ├─ package.json / package-lock.json      # Angular 19.2, npm (lockfileVersion 3)
   ├─ angular.json                          # targets: build + serve (SEM test, SEM lint)
   ├─ tsconfig*.json
   └─ src/  (64 arquivos .ts)               # standalone components + signals; 1 environment.ts
```

- Solução Clean Architecture: 5 projetos de produção + 1 de testes.
- 17 migrations EF em `backend/src/AegisScore.Infrastructure/Migrations/`
  (de `20260704224659_InitialCreate` a `20260719174626_NormalizeIdentityAccount`) + snapshot do modelo.
- Ausências confirmadas: sem `Directory.Build.props/targets`, `Directory.Packages.props`,
  `NuGet.config`, `.editorconfig`, `.config/dotnet-tools.json`, `Dockerfile`/`docker-compose`,
  `.github/workflows` (ou qualquer CI).

## 3. Stack e versões

| Componente | Tecnologia | Versão exigida/documentada | Versão local instalada | Evidência |
|---|---|---|---|---|
| SDK .NET | .NET SDK | `10.0.100` (+ latestMinor) | `10.0.300` | `global.json`; `dotnet --list-sdks` |
| Runtime | Microsoft.NETCore.App | `net10.0` | `10.0.8` | `.csproj`; `dotnet --list-runtimes` |
| Runtime web | Microsoft.AspNetCore.App | `net10.0` | `10.0.8` (e `8.0.28` presente) | idem |
| ORM | EF Core / Npgsql | `8.0.6` / `8.0.4` | (pacotes NuGet) | `AegisScore.Infrastructure.csproj` |
| Auth | JwtBearer / IdentityModel | `10.0.0` / `8.19.1` | (pacotes NuGet) | `AegisScore.Api.csproj` |
| Frontend | Angular | `^19.2.0` | `^19.2.x` (lock) | `frontend/package.json` |
| Linguagem FE | TypeScript | `~5.7.2` | (lock) | `frontend/package.json` |
| Node.js | Node | **"18+"** (README/DEV) | **`v24.16.0`** | `node --version` |
| npm | npm | — | `11.13.0` | `npm --version` |
| Banco | PostgreSQL | `14+` | **não instalado** | `appsettings.json` (Npgsql); `psql` ausente |
| Container | Docker | — | **não instalado** | `docker` ausente; sem Dockerfile no repo |

### Notas de compatibilidade (a revisar posteriormente, sem falha observada)

- **Node.js `v24.16.0` × Angular 19:** o projeto documenta apenas "Node 18+" (piso). A versão instalada
  (`v24.16.0`) é superior a esse piso, **mas a compatibilidade oficial de Node 24 com Angular 19
  não foi validada** contra a matriz de suporte do Angular nesta etapa. Fato observado: o
  `npm run build` (produção) **concluiu com sucesso** neste ambiente. Registrar como
  "build observado com sucesso", não como "compatibilidade oficial confirmada".
- **.NET 10 (`net10.0`) × EF Core 8.0.x:** os projetos alvejam `net10.0` mas fixam a maioria dos
  pacotes Microsoft/EF em `8.0.x` (com `JwtBearer 10.0.0` no conjunto). **Não é uma incompatibilidade
  confirmada:** build e testes passaram sem falha. Registrar como **combinação que merece revisão
  posterior** (alinhamento de versões / governança de dependências — ver EP-07/AUD-061).

## 4. Comandos identificados

| Objetivo | Diretório | Comando | Origem |
|---|---|---|---|
| Restore (backend) | raiz | `dotnet restore backend\AegisScore.sln` | `.sln` / `.csproj` |
| Build (backend) | raiz | `dotnet build backend\AegisScore.sln -c Debug` | `.sln` |
| Testes (backend) | raiz | `dotnet test backend\AegisScore.sln` | `tests/*.csproj` (xUnit) |
| Formatação (backend) | raiz | `dotnet format backend\AegisScore.sln --verify-no-changes` | SDK bundled (informativo — ver §6) |
| Executar API | `backend\src\AegisScore.Api` | `dotnet run` | `DEV.md`; `launchSettings.json` (porta 5100) |
| Instalar (frontend) | `frontend` | `npm install` (ou `npm ci`) | `package.json` / lock v3 |
| Build (frontend) | `frontend` | `npm run build` (= `ng build`, produção) | `package.json` scripts + `angular.json` |
| Servir (frontend) | `frontend` | `npm start` (= `ng serve`, porta 5173) | `package.json`; `angular.json` |

## 5. Comandos executados e resultados

| Comando | Resultado | Erros / warnings | Alterou arquivos versionados? |
|---|---|---|---|
| `dotnet restore backend\AegisScore.sln` | Sucesso ("projetos atualizados") | nenhum | Não |
| `dotnet build backend\AegisScore.sln -c Debug --no-restore` | **Sucesso** | **0 erros, 1 warning** (`CS8604`) | Não (só `bin/obj` ignorados) |
| `dotnet test backend\AegisScore.sln --no-build` | **Aprovado: 219 / 219** (0 falhas, 0 ignorados, ~33s) | nenhum | Não |
| `dotnet format ... --verify-no-changes` | Exit 2 (deltas de whitespace) | Informativo (ver abaixo) | Não (modo verify) |
| `npm run build` (frontend, produção) | **Sucesso (exit 0)** — bundle inicial 581 KB / 138 KB transferência (~17s) | **4 warnings** de budget de estilo | Não (só `dist/` ignorado) |

**Verificação de integridade:** `git status --short` foi conferido após cada comando e permaneceu
limpo (apenas artefatos ignorados). HEAD inalterado.

**`dotnet format` — classificação informativa:** o projeto **não possui política de formatação
versionada** (não há `.editorconfig`). O exit 2 apenas indica que o código difere das regras
padrão do formatador; **não é um gate do projeto** e não representa falha. Nenhuma formatação foi
aplicada (usado `--verify-no-changes`).

## 6. Warnings conhecidos (baseline congelada)

Estes são os **únicos** warnings esperados no estado atual. Em PRs posteriores, qualquer warning
**novo** deve ser tratado como **regressão** ou **justificado explicitamente** na descrição do PR.

**Backend (1):**
- `CS8604` — possível argumento de referência nula para o parâmetro `account` em
  `UserManagementService.Project(User, IdentityAccount)`.
  Local: `backend/src/AegisScore.Infrastructure/Auth/UserManagementService.cs:178`.

**Frontend (4)** — budget `anyComponentStyle` (limite de warning 4 KB; nenhum atinge o limite de
erro de 8 KB):
- `src/app/components/scoring/control-compliance-card.component.ts` — ~7.07 KB
- `src/app/pages/document-hub.component.ts` — ~6.56 KB
- `src/app/app.component.ts` — ~4.62 KB
- `src/app/pages/asset-inventory.component.ts` — ~4.12 KB

## 7. Testes existentes

| Item | Situação |
|---|---|
| Projetos de teste | 1 — `AegisScore.Infrastructure.Tests` (backend) |
| Framework | xUnit 2.9.2 + FluentAssertions 6.12.0, sobre **SQLite in-memory** |
| Quantidade | **219 testes** (verificado por execução real) |
| Resultado | **219 aprovados / 0 falhas / 0 ignorados** |
| Dependências externas | Nenhuma (não requer PostgreSQL, rede ou segredos — IA em modo stub) |
| Lacunas | **Sem testes de frontend** (0 `*.spec.ts`); sem projeto de testes de API/E2E; cobertura não medida |

## 8. Dependências externas

| Serviço | Finalidade | Build? | Testes? | Config segura | Limitação |
|---|---|---|---|---|---|
| PostgreSQL 14+ | Persistência + migrations no boot (`MigrateAsync`) | Não | Não | Connection string via user-secrets (`DEV.md`) | Não instalado local → API não sobe aqui |
| Anthropic (Claude) | `IAiAssessmentService` (chat/advisory) | Não | Não (stub) | `Ai:ApiKey` (user-secrets) | Opcional; sem chave usa stub |
| Google (Gemini) | `ILLMClient` (avaliador de telemetria) | Não | Não (stub) | `AegisAi:ApiKey` (user-secrets) | Opcional; sem chave usa stub |
| JWT signing key | Emitir/validar tokens | Não | Não | `Jwt:SigningKey` (user-secrets) | Boot **aborta** sem chave ≥ 32 bytes |
| Armazenamento de docs | Document Hub (Govern) | Não | Não | Filesystem local (`document-store`) | Sem object storage externo |
| Fila/broker | Workers (análise/sync/snapshot) | Não | Não | **Canais em memória** | Não durável (achado AUD-050) |

Serviços **não usados**: Redis, SMTP, object storage, message broker externo.

## 9. Comandos não executados (e motivo)

| Comando | Motivo de não execução |
|---|---|
| `dotnet run` (API) | Requer PostgreSQL (ausente localmente) e user-secrets (`Jwt:SigningKey`); a execução ponta a ponta **não foi validada** |
| `dotnet ef migrations` / `database update` | Migrations são aplicadas **no boot** (`MigrateAsync`); `DEV.md` proíbe execução manual; sem `dotnet-ef` como tool manifest |
| `npm test` / `ng test` | **Não configurado** (sem script `test`, sem target de teste em `angular.json`) |
| `npm run lint` / `ng lint` | **Não configurado** (sem script `lint`, sem eslint) |

> Regra: nenhum resultado positivo é presumido para comando não executado.

## 10. Limitações da validação

1. **Execução da API não validada** (sem PostgreSQL/Docker/psql local). Apenas build e testes
   foram exercitados; testes usam SQLite in-memory.
2. **Compatibilidade oficial de Node 24 × Angular 19 não validada** (apenas build observado; ver §3).
3. **Sem CI:** a validação é local; não há garantia de reprodução idêntica em pipeline (inexistente).
4. **Referência remota:** confirmada em sincronia via `fetch` autorizado em 2026-07-20; pode defasar
   novamente sem novo `fetch`.

## 11. Falhas preexistentes

Nenhuma **falha de build ou de teste** no estado atual. As ocorrências abaixo são warnings/lacunas
preexistentes (não corrigidas neste PR):

| ID | Descrição | Local | Disposição |
|---|---|---|---|
| PR0-BL-01 | Warning `CS8604` (nullable) | `Infrastructure/Auth/UserManagementService.cs:178` | Congelado (§6); tratar em EP-01 |
| PR0-BL-02 | 4 warnings de budget CSS | componentes do frontend (§6) | Congelado (§6); EP-05 |
| PR0-BL-03 | `dotnet format` não-limpo (sem `.editorconfig`) | solução backend | Informativo; decisão futura de política de formato |
| PR0-BL-04 | Sem CI/CD | repositório | AUD-056 — PR separado |
| PR0-BL-05 | Sem testes/lint de frontend | `frontend/` | AUD-033 — EP-05 |
| PR0-BL-06 | Credencial default no `appsettings.json` (par usuário/senha trivial; **valor mascarado**) | `Api/appsettings.json` | AUD-057 — EP-00 |
| PR0-BL-07 | Drift de documentação (README diz ".NET 8"/porta 5080/EnsureCreated; DEV afirma connection string vazia; AEGIS_STATE descreve working tree já commitado) | docs raiz | AUD-031 — EP-00 |
| PR0-BL-08 | PostgreSQL/Docker/psql ausentes | ambiente local | Limitação de ambiente (não é falha de código) |

## 12. Estado de CI/CD

- **Existente:** nada.
- **Ausente (comprovado):** sem `.github/workflows`, GitLab CI, Azure Pipelines, Jenkinsfile,
  CircleCI ou qualquer `*.yml`/`*.yaml` de pipeline; sem `Dockerfile`/`docker-compose`; sem
  manifesto de ferramentas dotnet.
- **Não determinado:** automação eventual configurada no lado remoto do GitHub (fora do repositório).
- Correlação: **AEGIS-AUD-056**. Implementar CI/CD excede um PR pequeno → **PR separado**.

## 13. Referência às pendências do Plano Diretor

Este baseline **não remedia** nada. Achados do Plano Diretor **confirmados por inspeção/execução**
nesta etapa (para rastreabilidade; correção nas fases indicadas):

| Achado | Descrição | Fase |
|---|---|---|
| AEGIS-AUD-050 | Filas em memória (`Channel*`) não duráveis | EP-00 |
| AEGIS-AUD-051 | Workers (`HostedService`) dentro do processo da API | EP-06 |
| AEGIS-AUD-052 | Migrations aplicadas no boot concorrente da API (`MigrateAsync`) | EP-00 |
| AEGIS-AUD-053 | `AddDataProtection()` sem persistência de key ring | EP-00 |
| AEGIS-AUD-056 | Ausência de CI/CD e supply chain | EP-00 / EP-07 |
| AEGIS-AUD-057 | Credencial default no arquivo de configuração | EP-00 |
| AEGIS-AUD-058 | CORS com origens fixas (não configurável por ambiente) | EP-01 |
| AEGIS-AUD-031 | Documentação arquitetural desalinhada com o estado real | EP-00 |
| AEGIS-AUD-033 | Ausência de suíte de testes de frontend | EP-05 |

Sequência de remediação, gates e critérios de aceite: ver `Plano_Diretor_AEGIS_v1.md`
(EP-00 → EP-07; gates G0 → G7). O PR 0 é a preparação de baseline anterior ao EP-00.

## 14. Reprodução

No estado limpo do repositório (working tree sem alterações):

```powershell
# Backend
dotnet restore backend\AegisScore.sln
dotnet build   backend\AegisScore.sln -c Debug
dotnet test    backend\AegisScore.sln

# Frontend (node_modules já presente; use "npm ci" para instalação reproduzível)
cd frontend
npm run build
```

Resultado esperado: backend compila (0 erros, 1 warning `CS8604`), testes 219/219,
frontend build exit 0 com os 4 warnings de budget conhecidos. Qualquer desvio deve ser
investigado como possível regressão ou mudança de ambiente.
