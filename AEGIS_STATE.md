# AEGIS_STATE.md — Snapshot Arquitetural Tático

> **Propósito deste arquivo:** reinjeção de contexto exato em futuras sessões de IA, sem reler o código-fonte. Não é documentação comercial. Última atualização: **2026-07-11**.
>
> **Base de versionamento:** trabalho da sessão (Identify → Recover + frontend) está no **working tree, NÃO commitado**, por cima do commit `dcedf57` (branch `feat/telemetry-ingestion-scoring-consolidation`). O Felipe versiona manualmente.

---

## 1. Visão Geral e Propósito

**Aegis Score** é uma plataforma de **Secure Score corporativo** baseada no **NIST CSF 2.0**. Traduz evidência técnica real (telemetria de SOC multicloud + documentos de governança) num score de postura por controle NIST, agregável por Função/Categoria (modelo Microsoft Secure Score).

- **Backend:** .NET 10 + PostgreSQL, Clean Architecture (`AegisScore.{Api, Application, Domain, Infrastructure, Connectors.Microsoft}`).
- **Frontend:** Angular 19 (standalone + signals), tema HUD dual-neon.
- **Multitenancy Secure-by-Design:** EF Core Global Query Filters + stamping fail-closed no `SaveChanges` (`ITenantOwned`), tenant resolvido do claim `tenant_id` do JWT (`HttpTenantContext`), nunca de header spoofável. `TenantConsistencyMiddleware` barra divergência token×header (403).
- **Duas fontes de evidência, um único ledger** (`TenantControlState`, célula tenant×subcategoria):
  - **Telemetria** (`IAegisAiEvaluatorService.EvaluateAsync`) — AUTORITATIVA, pode levar a 100%.
  - **Documental** (`DocumentAnalysisWorker`, Govern) — teto de 50% (`MitigatedByThirdParty`).

---

## 2. Estado do Backend (.NET)

### 2.1 Fluxo de avaliação (o coração)

```
Telemetria HTTP → TelemetryController → ITelemetryIngestionService
  → IAegisAiEvaluatorService.EvaluateAsync → ILLMClient (Stub|Gemini)
  → IControlStateWriter.ApplyVerdictAsync → TenantControlState (fonte Telemetry)
```
- **Escritor único do ledger:** `ControlStateWriter` (Infrastructure/Scoring) concentra a regra status→pontos e o upsert idempotente. Telemetria e documento gravam pela mesma porta `IControlStateWriter` — nunca reimplementam scoring.
- **Precedência por FONTE** (`TenantControlState.LastVerdictSource`, enum `VerdictSource`): `Telemetry` sobrescreve sempre (inclusive rebaixando); `Documentary` só faz UPGRADE e nunca sobrescreve telemetria. Um PDF jamais maquia um `NonCompliant` técnico nem derruba um `Compliant` de telemetria.
- **Scoring:** `CurrentScore = round(MaxScorePoints × fator)`; fator: `Compliant`=1.0, `MitigatedByThirdParty`=0.5, `NonCompliant`=0. Pesos `MaxScorePoints` por tier de categoria: **20** (PR.AA, PR.DS), **15** (PR.PS, PR.IR, DE.CM, ID.RA, GV.SC), **5** (GV.*, RS.CO, RC.CO), **10** (demais). ⚠️ arredondamento bancário: 50% de peso ímpar (5) = 2/5 = **40% efetivo**.

### 2.2 Refatoração DRY — `CategoryTelemetrySignal`

Antes havia `ProtectTelemetrySignal` e `DetectTelemetrySignal` idênticos. Unificados em **um** modelo na camada Application:

```csharp
public record CategoryTelemetrySignal(
    string SubcategoryCode, string Pillar, string Category, IReadOnlyList<string> Metrics);
```
- **Um método:** `ITelemetryIngestionService.IngestCategoryAsync(CategoryTelemetrySignal)`.
- **Um compositor:** `ComposeCategoryPayload` → `"{Pillar} / {Category} (control {code}) telemetry:\n{métricas}"`.
- **Controller:** helper único `IngestCategory(code, pillar, category, metrics)` + `RunAsync` (mapeia código NIST inexistente → 400, projeta veredito no `TelemetryVerdictDto`). Cada rota de pilar é uma expressão de uma linha.
- O payload gerado é **idêntico** ao anterior → StubLlmClient não mudou; testes de Protect/Detect passaram sem alterar lógica.

### 2.3 Mapa de rotas (as 6 Funções NIST)

| Função | Ingestão (escrita no ledger) | Controle-alvo (real, catálogo) |
|---|---|---|
| **GV** Govern | Documental: `POST /api/v1/governance/documents` (+ worker) | GV.PO-01, GV.RR-01 (via PDF) |
| **ID** Identify | `POST /api/v1/telemetry/asset` | ID.AM-01 |
| **PR** Protect | `POST /api/v1/telemetry/protect/{identity,data,platform,network}` | PR.AA-01, PR.DS-01, PR.PS-01, PR.IR-01 |
| **DE** Detect | `POST /api/v1/telemetry/detect/{anomalies,monitoring,process}` | DE.AE-02, DE.CM-01, DE.AE-06 |
| **RS** Respond | `POST /api/v1/telemetry/respond/{analysis,mitigation}` | RS.MA-01, RS.MI-01 |
| **RC** Recover | `POST /api/v1/telemetry/recover/execution` | RC.RP-01 |

**Leitura do HUD (consolidado, prefixo canônico `scoring`):**
`GET /api/v1/scoring/{current, trend, pending, dashboard}` — tenant implícito via Global Query Filter. (O antigo `aegis-score` foi consolidado em `scoring`.)

**Genérico + utilitários DEBUG:** `POST /api/v1/telemetry/ingest` (payload livre); `POST /api/v1/dev/{seed-demo, seed-user, reprocess-document}` (anônimos, `#if DEBUG`).

⚠️ **Divergências CSF 2.0 já validadas (não redescobrir):** o **catálogo JSON já está completo** (6 funções / 106 subcats — `Data/nist_csf_2_0_catalog.json`); **NÃO adicionar controles ao `FrameworkSeeder`**. `DE.DP` foi **removido** no 2.0 (absorvido em DE.AE; `DE.AE-06` herda `DE.DP-4`). `DE.AE-01` **não existe** (começa em `-02`).

### 2.4 `StubLlmClient` (motor DEV, sem rede)

`ILLMClient` determinístico usado quando **não há `AegisAi:ApiKey`**. Faz **parsing numérico real via regex** (helpers `Num(label)` e `Flag(label)` sobre o payload lowercased), não só keyword-matching. Roteia por família de rótulos: `EvaluateProtect` → `EvaluateDetect` → `EvaluateRespondRecover` → genérico. Cada categoria é **binária** (falha em qualquer condição = `NonCompliant`; passa em tudo = `Compliant`). Motor real: `GeminiLlmClient` (modelo **`gemini-2.0-flash`**, corrigido do `1.5-flash` aposentado; falha HTTP → `AiUnavailableException`/503).

### 2.5 Testes — **67/67 verdes**

`AegisScore.Infrastructure.Tests` (xUnit + FluentAssertions 6.12, SQLite in-memory). Cobrem: precedência de fonte (`ControlStateWriterTests`), o fluxo real ingestão→motor→writer com `StubLlmClient` (`TelemetryIngestionServiceTests`, `AssetTelemetryTests`), transporte Gemini (`GeminiLlmClientTests`), dedupe de documentos de governança sob corrida (`GovernanceDocumentDedupeTests`, ver 2.6). O catálogo de teste semeia PR.AA-01, DE.CM-01, RS.MA-01, RS.MI-01, RC.RP-01, ID.AM-01.

### 2.6 Govern — dedupe de documentos agora é invariante de banco (sessão 2026-07-11)

O índice `(TenantId, Sha256)` de `GovernanceDocument` era **não único** — os dois caminhos de ingestão (`GovernanceDocumentsController.Upload` e `PolicyIngestionWorker.SyncTenantAsync`) faziam um `AnyAsync` (checa hash) → `Add` clássico read-then-write, então uma corrida (dois uploads simultâneos do mesmo arquivo, ou o ciclo periódico do worker batendo com o gatilho `/sync` sob demanda no mesmo tenant) podia gravar o mesmo documento duas vezes. O worker mitigava isso com um `SemaphoreSlim _syncGate` 1×1 — um paliativo em memória, inútil entre réplicas/processos.

**Correção — a idempotência virou uma invariante do banco:**
- `AegisScoreDbContext.OnModelCreating`: índice agora `.IsUnique()`, **parcial** (`WHERE "Sha256" IS NOT NULL` — o caminho `/connect` registra o documento de integração antes de anexar o binário, então vários registros sem hash ainda precisam conviver).
- `Upload` e `SyncTenantAsync`: o `AnyAsync` prévio virou só o fast-path; o `SaveChangesAsync` do insert ganhou `catch (DbUpdateException)` tratado como idempotente (mesmo padrão já usado em `AegisScoreSnapshotWorker.CaptureTenantAsync`) — no `Upload` vira 409 Conflict; no worker, `db.Entry(doc).State = EntityState.Detached` (o `db` é reusado no `foreach` do lote) + log warning + `continue`.
- **`SemaphoreSlim _syncGate` e `GuardedSyncAsync` foram REMOVIDOS** do `PolicyIngestionWorker` — deixaram de ser necessários para correção (o banco impõe o dedupe) e tenants distintos agora sincronizam em paralelo sem lock global desnecessário.
- Migration `20260711222804_UniqueGovernanceDocumentSha256`: dedupe defensivo de linhas pré-existentes (`DELETE` via `ROW_NUMBER() OVER (PARTITION BY TenantId, Sha256 ORDER BY CreatedAt, Id)`, mantém a mais antiga; `ControlMappings` somem via `ON DELETE CASCADE`) antes do `CREATE UNIQUE INDEX` — no-op numa base já limpa. **Aplicada ao banco `aegis_dev`** via `dotnet ef database update` nesta sessão (⚠️ note: `aegis_dev` é o banco real configurado em user-secrets — diverge do `aegis` do `appsettings.json` versionado citado na seção 5/DEV.md; confirmar qual é o nome correto antes de instruções futuras).
- Teste novo `GovernanceDocumentDedupeTests` prova a corrida real (dois contexts, ambos leem "não existe" antes de qualquer commit, o segundo `SaveChangesAsync` lança `DbUpdateException`, sobra exatamente 1 documento), o filtro parcial (docs sem hash convivem) e o isolamento tenant-leading (mesmo hash em tenants diferentes não colide).

Ainda **não commitado** (segue a convenção da seção topo deste arquivo — o Felipe versiona manualmente).

---

## 3. Dicionário de Heurísticas (Tolerância Zero)

Regra de negócio implacável de cada categoria no `StubLlmClient`. **Reprova (`NonCompliant`) se qualquer condição for verdadeira**; caso contrário `Compliant`.

| Controle | Categoria | Reprova (NonCompliant) se |
|---|---|---|
| **ID.AM-01** | Asset (Identify) | `EdrCoverage == Absent` **ou** `OsLifecycle == EndOfLife` — *(EDR Active + 0 CVE = Compliant; intermediário = MitigatedByThirdParty 50%)* |
| **PR.AA-01** | Identity | `PrivilegedMfaCoverage < 100` **ou** `!ConditionalAccessEnforced` — *privilégio sem MFA é falha crítica* |
| **PR.DS-01** | Data | `EndpointEncryptionCoverage < 95` **ou** `UnencryptedTrafficDetected` |
| **PR.PS-01** | Platform | `CisBenchmarkComplianceRate < 80` **ou** `MissingCriticalPatchesCount > 0` |
| **PR.IR-01** | Network | `!DefaultDenyFirewallEnforced` |
| **DE.AE-02** | Anomalies | `UninvestigatedHighAnomaliesCount > 0` **ou** `FalsePositiveRate > 50` — *fadiga de alerta* |
| **DE.CM-01** | Monitoring | `CriticalLogSourceCoverage < 95` **ou** `UnmonitoredCriticalAssetsCount > 0` — *ativo crítico cego* |
| **DE.AE-06** | Detection Eng. | `MitreAttckCoverageRate < 40` **ou** `SimulatedAttacksDetectedRate < 80` |
| **RS.MA-01** | Incident Analysis | `MeanTimeToAcknowledgeMins > 30` **ou** `ThreatHuntingCoverageRate < 80` |
| **RS.MI-01** | Incident Mitigation | `!AutomatedIsolationEnabled` **ou** `MeanTimeToRespondMins > 120` |
| **RC.RP-01** | Recovery Plan | `!ImmutableBackupsEnabled` **ou** `BackupIntegrityStatus != "Valid"` **ou** `!RecoveryTimeObjectiveMet` — *resiliência a ransomware* |

*(GV é avaliado por documento, não por heurística de telemetria: PDF → teto 50%.)*

---

## 4. Estado do Frontend (Angular 19)

- **Menu lateral (`app.component.ts`):** as **6 Funções NIST estão ativas e roteáveis**. Protect/Detect/Respond/Recover foram destravadas hoje (`<span class="nav-item soon">` → `<a routerLink>`, iguais a Identify/Govern). Estilos `.soon` ficaram órfãos (removíveis).
- **Rotas (`app.routes.ts`):** adicionadas `/protect`, `/detect`, `/respond`, `/recover` (com `authGuard` + title), abaixo de `governance`.
- **4 componentes standalone novos** em `src/app/pages/`: `ProtectDashboardComponent`, `DetectDashboardComponent`, `RespondDashboardComponent`, `RecoverDashboardComponent`. **Esqueletos**: `<h1>` do pilar + subtítulo com as subcategorias + card com tag "Em construção". Template/estilos inline, tema HUD (`var(--text/--muted/--line/--cyan)`, card `rgba(122,145,190,.04)`).
- **Sem HTTP ainda** — apenas renderizam. `ng build` verde (só os 2 warnings de budget CSS pré-existentes de `document-hub` e `auditor-chat`).
- Telas pré-existentes que já consomem API: Dashboard executivo, Aegis Score HUD (`/scoring/trend`+`/current`), Inventário (`/assets` — só GET), Govern/Document Hub.

---

## 5. Ponto de Parada e Backlog

**Onde paramos:** backend do NIST **completo e provado ao vivo** (12 endpoints de telemetria, 5 pilares por telemetria + Govern por documento, 67/67 testes) + dedupe de documentos de governança agora garantido por índice único no banco (ver 2.6). Frontend com as 6 abas acesas e painéis-esqueleto renderizando. **Nada commitado** desta sessão (nem da anterior).

**Próximos passos imediatos (prioridade):**

1. **`SharePointProvider` ainda é STUB** (`AegisScore.Connectors.Microsoft/SharePointProvider.cs`) — o Provider Pattern JÁ EXISTE (`IDocumentIntegrationFactory`/`IDocumentIntegrationProvider`, `PolicyIngestionWorker` agnóstico, gancho `POST /governance/documents/connect`), mas devolve políticas MOCKADAS em vez de chamar o Microsoft Graph. Falta: autenticação OAuth client credentials (segredos em `ConnectorConfig.EncryptedSettings`) + `GET /sites/{id}/drive/root/children` real. *(Correção da desatualização: o item anterior deste backlog descrevia esse padrão como algo a construir do zero — já está construído, falta só ligar a fonte real.)*
2. **Integração HTTP do frontend** — os 4 painéis PR/DE/RS/RC precisam consumir `GET /api/v1/scoring/dashboard`, filtrar por Função/prefixo de categoria e listar os controles `NonCompliant` (gráficos nativos canvas/SVG, sem libs, padrão do projeto).
3. **Ligar o motor real** — `GeminiLlmClient` (`gemini-2.0-flash`) **não testado contra o Google** (quota/chave do Felipe); a prova ao vivo usa o Stub forçado (`$env:AegisAi__ApiKey=' '`).
4. **HUD `/trend`** só preenche via `AegisScoreSnapshotWorker` (foto à meia-noite UTC com API no ar); considerar snapshot no boot em DEV.

**Ambiente de execução (DEV):** API em `http://localhost:5100` (`dotnet run --launch-profile http`). ⚠️ Banco real (via `dotnet user-secrets`, confirmado nesta sessão por `dotnet ef migrations list`) é **`aegis_dev`** — diverge do `Database=aegis` que aparece vazio-de-propósito no `appsettings.json` versionado (DEV.md Passo 1 usa `aegis`; o user-secret local do Felipe aponta para `aegis_dev`). DemoTenant `aa000000-0000-0000-0000-000000000001`; usuário `analista@demo.aegis` / `Aegis@12345` (via `POST /dev/seed-user`). Segredos (JWT, connection string, `AegisAi:ApiKey`) em `dotnet user-secrets` — ver `DEV.md` na raiz. **Schema é aplicado automaticamente no boot** (`db.Database.MigrateAsync()` em `Program.cs`) — não rodar `dotnet ef database update` à mão em uso normal (exceção: aplicação pontual de uma migration nova fora do ciclo de boot, como feito nesta sessão).
