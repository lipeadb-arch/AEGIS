# AEGIS_STATE.md — Snapshot Arquitetural Tático

> **Propósito deste arquivo:** reinjeção de contexto exato em futuras sessões de IA, sem reler o código-fonte. Não é documentação comercial. Última atualização: **2026-07-20**.
>
> **Estado verificado após o merge do PR #1 (2026-07-20):** a **`main`** estava em **`c3a0bd3`**, sincronizada com `origin/main` (0/0). Esta é uma constatação histórica daquele momento, não uma afirmação permanente: **o estado atual deve sempre ser confirmado com `git status` no início de cada sessão.** **Todas as notas de arco abaixo (arcos 13–23) que dizem "no working tree" agora são HISTÓRICO — o código correspondente já está COMMITADO na `main`** (build verde; testes **219/219** revalidados em 2026-07-20). Novidade deste ciclo: o **PR #1** (squash-merge → `c3a0bd3`) adicionou **`docs/pr0-baseline.md`** — a linha de base técnica do PR 0: matriz de versões (dotnet SDK 10.0.300 / net10.0, Angular 19.2, Node 24, npm 11; PostgreSQL/Docker ausentes no ambiente local), comandos oficiais, resultados de build/teste, **warnings congelados** (1× `CS8604` no backend + 4 budgets CSS no frontend), **testes 219/219**, dependências externas, **CI/CD ainda ausente** e rastreabilidade das pendências `AEGIS-AUD-*`. A branch de trabalho `chore/pr0-baseline` foi mergeada e **removida** (local e remota); `feat/telemetry-ingestion-scoring-consolidation` segue apenas local, fora da base.
>
> **Base de versionamento:** ⚠️ **MUDOU — a nota anterior desta seção estava obsoleta.** Todo o trabalho das sessões anteriores (Identify → Recover + frontend vivo + Copiloto GRC + ID.RA/Raio de Explosão + Ingestão de Identidade do Entra ID + Checklist Dinâmico + Advisories) **JÁ ESTÁ COMMITADO** na branch **`main`** — HEAD `3fd88d7` (2026-07-16); o commit `dcedf57` citado antes é ancestral de `main`, e a branch `feat/telemetry-ingestion-scoring-consolidation` ficou para trás (existe local, não é mais a base). O trabalho no working tree é o da **seção 10** (enriquecimento do contrato de controle para a IA — 20 arquivos) **+ o da seção 11** (Teste de Fogo do RAG: `RagFireTestScenarios.cs` e `DevRagFireTestController.cs` novos; `AegisAiOptions.cs` e `appsettings.json` com o modelo trocado). O andaime de mock do frontend (10.4) **foi removido** — a reversão também está no working tree. Somam-se ainda os **3 arquivos de frontend da seção 12** (`app.component.ts`, `asset-inventory.component.ts`, `document-hub.component.ts` — polimento de UI/UX) **e todo o arco das seções 13–18** (persona do Auditor, MissingRequirements, TTL de sinal, RAG documental de 2 passadas, resiliência Polly e auditoria do Executivo). O Felipe versiona manualmente.
>
> **Arquivos NOVOS do arco 23 — Central de Integrações** (2026-07-19, no working tree): frontend — `models/connector.models.ts`, `services/connector.service.ts`, `pages/integrations.component.ts`. **Modificados:** backend — `Application/Services/ITenantManagementService.cs` (`ConnectorSummary` + `ListConnectorsAsync`), `Infrastructure/Tenancy/TenantManagementService.cs`, `Api/Controllers/{ConnectorsController,TenantsController}.cs`, `Api/Contracts/Dtos.cs`; frontend — `app.routes.ts`, `app.component.ts` (nav "Configuração"), **`package.json` ⚠️ `@angular/forms` NOVO**. Sem migration.
>
> **Arquivos NOVOS do arco 22 — SSO simulado** (2026-07-19, no working tree): backend — migration `20260719174626_NormalizeIdentityAccount`; frontend — `components/tenant-switcher.component.ts`. **Modificados:** `Domain/Auth.cs` (`IdentityAccount` nova + `User` virou membership), `Infrastructure/Persistence/AegisScoreDbContext.cs`, `Infrastructure/Auth/{AuthService,JwtTokenService,UserManagementService}.cs`, `Application/Auth.cs`, `Api/Controllers/{AuthController,UsersController,DevController}.cs`, `Api/Contracts/Dtos.cs`, `tests/Auth/UserManagementServiceTests.cs`; frontend — `app.component.ts`, `services/auth.service.ts`, `interceptors/auth.interceptor.ts`, `environments/environment.ts` (⚠️ `tenantId` REMOVIDO), `services/{aegis-score,asset,dashboard,governance}.service.ts` (limpeza do X-Tenant hardcoded). ✅ Migration **APLICADA** em `aegis_dev`.
>
> **Arquivos NOVOS do arco 21 — identidades** (2026-07-19, no working tree): backend — `Application/Services/IUserManagementService.cs`, `Infrastructure/Auth/UserManagementService.cs`, `Api/Controllers/UsersController.cs`; testes — `Tests/Auth/UserManagementServiceTests.cs`. **Modificados:** `Api/Contracts/Dtos.cs`, `Infrastructure/DependencyInjection.cs`. **Sem migration** — o domínio já bastava.
>
> **Arquivos NOVOS do arco 20 — onboarding** (2026-07-19, no working tree): backend — `Application/Services/ITenantManagementService.cs`, `Infrastructure/Tenancy/TenantManagementService.cs`, migration `20260719122213_UniqueConnectorConfigNaturalKey`; testes — `Tests/Tenancy/TenantManagementServiceTests.cs`. **Modificados:** `Api/Contracts/Dtos.cs`, `Api/Controllers/TenantsController.cs`, `Api/Controllers/ConnectorsController.cs`, `Infrastructure/DependencyInjection.cs`, `Infrastructure/Persistence/AegisScoreDbContext.cs`. ✅ **Migration APLICADA** em `aegis_dev` (ver §20.7 — o startup a aplicou sozinho).
>
> **Arquivos NOVOS do arco 13–19** (para conferir num `git status`): backend — `Api/Data/AuditorPersonality.json`, `Application/Services/IAuditorPersonaProvider.cs`, `Application/Assessment/RuleEvaluator.cs`, `Application/Documents/DocumentChunker.cs`, `Infrastructure/Ai/AuditorPersonaProvider.cs`, `Infrastructure/Ai/AiResilienceExtensions.cs`, `Infrastructure/Scoring/ScoringOptions.cs`, migration `*_ControlState_MissingRequirements`; frontend — `components/scoring/missing-requirements.component.ts`, `components/scoring/aegis-pillar-checklist.component.ts`, `components/scoring/gap-balance.component.ts`, `components/scoring/blast-radius-summary.component.ts`; testes — `AuditorPersonaTests`, `RuleEvaluatorTests`, `MissingRequirementsTests`, `MissingRequirementScenarioTests`, `SignalFreshnessTests`, `DocumentRagTests`, `AiResilienceTests`.

---

## 1. Visão Geral e Propósito

**Aegis Score** é uma plataforma de **Secure Score corporativo** baseada no **NIST CSF 2.0**. Traduz evidência técnica real (telemetria de SOC multicloud + documentos de governança) num score de postura por controle NIST, agregável por Função/Categoria (modelo Microsoft Secure Score).

- **Backend:** .NET 10 + PostgreSQL, Clean Architecture (`AegisScore.{Api, Application, Domain, Infrastructure, Connectors.Microsoft}`).
- **Frontend:** Angular 19 (standalone + signals), tema HUD dual-neon.
- **Multitenancy Secure-by-Design:** EF Core Global Query Filters + stamping fail-closed no `SaveChanges` (`ITenantOwned`), tenant resolvido do claim `tenant_id` do JWT (`HttpTenantContext`), nunca de header spoofável. `TenantConsistencyMiddleware` barra divergência token×header (403).
- **Duas fontes de evidência, um único ledger** (`TenantControlState`, célula tenant×subcategoria):
  - **Telemetria** (`IAegisAiEvaluatorService.EvaluateAsync`) — AUTORITATIVA, pode levar a 100%. **Agora cobre também Govern** (GV.SC/GV.RR — ver 2.7).
  - **Documental** (`DocumentAnalysisWorker`, Govern) — teto de 50% (`MitigatedByThirdParty`).
  - **Telemetria ATIVA de identidade** (`IdentityTelemetryController` → `IEntraIdTelemetryProvider`) — o Aegis PUXA a postura do Entra ID e a avalia em PR.AA-01 + GV.RR-01, ponderando **controles compensatórios de rede** (OT/IoT) — ver 2.10.
- **Copiloto GRC onipresente** (Auditor Virtual): chat com consciência de contexto de rota + Agentic Routing, disponível em toda a plataforma (ver 2.8 e seção 4).

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
- **Controller:** helper único `IngestCategory(code, pillar, category, metrics)` + `RunAsync` (mapeia código NIST inexistente → 400, projeta veredito no `TelemetryVerdictDto`). Cada rota de pilar é uma expressão de uma linha. **Govern (GV.SC/GV.RR) reusa o MESMO seam** (pilar "Govern") — ver 2.7.

### 2.3 Mapa de rotas (as 6 Funções NIST)

| Função | Ingestão (escrita no ledger) | Controle-alvo (real, catálogo) |
|---|---|---|
| **GV** Govern | Documental: `POST /governance/documents` (upload · `/sync` · worker) · **Telemetria: `POST /telemetry/govern/{supply-chain,roles}`** | GV.PO-01/GV.RR-01 (doc, teto 50%) · **GV.SC-01, GV.RR-01 (telemetria, autoritativa)** |

**Onboarding (§20):** `POST /api/v1/tenants` (PlatformAdmin) · `POST /api/v1/tenants/{business-units,processes,connectors}` (TenantAdmin; o de conectores é UPSERT) · **`POST /api/v1/connectors/{connectorId}/{test,sync}`** — ⚠️ rota MUDOU: era `tenants/{tenantId}/connectors/{connectorId}/…` e era ANÔNIMA (ver §20.2).
| **ID** Identify | `POST /api/v1/telemetry/asset` (ID.AM) · **`POST /api/v1/risk-assessment/{assetId}/blast-radius`** (ID.RA — Raio de Explosão, ver 2.9) | ID.AM-01 · **ID.RA-01/05 (via hook do raio)** |
| **PR** Protect | `POST /api/v1/telemetry/protect/{identity,data,platform,network}` · **`POST /api/v1/telemetry/identity/entra-id`** (Entra ID ATIVO → PR.AA-01 + GV.RR-01, ver 2.10) | PR.AA-01, PR.DS-01, PR.PS-01, PR.IR-01 |
| **DE** Detect | `POST /api/v1/telemetry/detect/{anomalies,monitoring,process}` | DE.AE-02, DE.CM-01, DE.AE-06 |
| **RS** Respond | `POST /api/v1/telemetry/respond/{analysis,mitigation}` | RS.MA-01, RS.MI-01 |
| **RC** Recover | `POST /api/v1/telemetry/recover/execution` | RC.RP-01 |

**Leitura do HUD (consolidado, prefixo canônico `scoring`):**
`GET /api/v1/scoring/{current, trend, pending, dashboard}` — tenant implícito via Global Query Filter. O `dashboard` devolve a **lista plana** de todos os controles avaliados (`TenantControlStateDto`: `subcategoryCode, scorePoints, maxScorePoints, controlStatus, aiEvidence, lastEvaluatedAt, lastVerdictSource`) — o frontend filtra por pilar pelo **prefixo do código**.

**Govern — ingestão de documentos (Provider Pattern):** `POST /governance/documents/sync` (gatilho MANUAL, **202 Accepted**) → `IPolicySyncTrigger` (canal em memória `ChannelPolicySyncTrigger`) → `PolicyIngestionWorker` consome e faz o **fetch agnóstico** via `IDocumentIntegrationFactory`/`IDocumentIntegrationProvider` (ver 2.7). Capability nova: `ConnectorCapability.PolicyDocuments`.

**Copiloto GRC:** `POST /api/v1/auditor/chat` (escopado por contexto + Agentic Routing — ver 2.8). Entrevista GRC legada: `POST /api/v1/governance/interviews` (`GrcInterviewController`) — hoje **órfã na UI** até a reativação via Agentic Routing (backlog).

**Genérico + utilitários DEBUG:** `POST /api/v1/telemetry/ingest` (payload livre); `POST /api/v1/dev/{seed-demo, seed-user, reprocess-document}` (anônimos, `#if DEBUG`).

⚠️ **Divergências CSF 2.0 já validadas (não redescobrir):** o **catálogo JSON já está completo** (6 funções / 106 subcats — `Data/nist_csf_2_0_catalog.json`); **NÃO adicionar controles ao `FrameworkSeeder`**. `GV.SC-01` e `GV.RR-01` **já existem** no catálogo. `DE.DP` foi **removido** no 2.0 (absorvido em DE.AE; `DE.AE-06` herda `DE.DP-4`). `DE.AE-01` **não existe** (começa em `-02`).

### 2.4 `StubLlmClient` (motor DEV, sem rede)

`ILLMClient` determinístico usado quando **não há `AegisAi:ApiKey`**. Faz **parsing numérico real via regex** (helpers `Num(label)` e `Flag(label)` sobre o payload lowercased), não só keyword-matching. Roteia por família de rótulos: `EvaluateProtect` → `EvaluateDetect` → `EvaluateRespondRecover` → **`EvaluateGovern`** → genérico. ⚠️ `EvaluateGovern` VEM ANTES do fallback genérico de propósito: o rótulo `"Third Party Audited:"` contém `"third party"`, que o genérico casaria como `MitigatedByThirdParty` e mascararia o veredito real de GV.SC. Cada categoria é **binária** (falha em qualquer condição = `NonCompliant`; passa em tudo = `Compliant`) — **exceto a telemetria de identidade do Entra ID, de 3 vias** (Compliant/`MitigatedByThirdParty`/NonCompliant, ver 2.10), que ancora no CÓDIGO do controle via `TargetsControl(p, "pr.aa"|"gv.rr")` (o MESMO retrato alimenta PR.AA-01 e GV.RR-01, então a âncora impede a regra de um decidir o outro). Motor real: `GeminiLlmClient` (modelo **`gemini-2.0-flash`**; falha HTTP → `AiUnavailableException`/503).

### 2.5 Testes — **219/219 verdes** (build da solução verde, 0 erros, em 2026-07-19)

> ⚠️ **Contagem atualizada:** o corpo desta seção descreve o estado histórico de **110** testes. O arco das seções **13–18** levou a suíte a **165** (110 → 111 → 115 → 120 → 126 → 130 → 137 → 146 → 158 → 160 → 165), a **§20** (onboarding) a **187**, a **§21** (identidades) a **210** e a **§22** (SSO simulado) a **219** — a §22 REESCREVEU vários casos da §21, cuja premissa "mesmo e-mail = identidades independentes" foi invertida pela conta global, e acrescentou `TenantSwitchingTests` (8 casos) para cobrir o `AuthService`. A **§23** não trouxe testes novos (ver 23.4). Os arquivos novos estão listados no cabeçalho; o detalhe de cada leva está na seção que o introduziu.

`AegisScore.Infrastructure.Tests` (xUnit + FluentAssertions 6.12, SQLite in-memory). Cobrem:
- precedência de fonte (`ControlStateWriterTests`);
- fluxo real ingestão→motor→writer com `StubLlmClient` (`TelemetryIngestionServiceTests` — inclui os 4 casos **GV.SC/GV.RR**; `AssetTelemetryTests`);
- transporte Gemini (`GeminiLlmClientTests`);
- Provider Pattern de ingestão documental (`DocumentIntegrationFactoryTests` — resolução de estratégia por `ConnectorProvider`);
- canal do gatilho de sync (`ChannelPolicySyncTriggerTests` — round-trip FIFO);
- dedupe de documentos sob corrida (`GovernanceDocumentDedupeTests`, ver 2.6);
- **Agentic Routing do Copiloto (18 casos novos, 67→85):** roteamento de intenção do `StubAssessmentService` (`StubAssessmentServiceTests` — COPILOT vs START_INTERVIEW por palavra-chave e por escopo; seed com `targetSubcategoryCode`) e o parsing estruturado + resiliência do `ClaudeAssessmentService` (`ClaudeAssessmentServiceTests` — `StubHttpMessageHandler` no envelope Anthropic; JSON malformado → COPILOT; chave no header `x-api-key`);
- **Motor de Raio de Explosão (9 casos novos, 85→94):** `BlastRadiusCalculatorTests` — traversal reverso, decaimento Hard/Soft/Redundant (simples e composto), terminação sob ciclos (A↔B e ciclo com o root), múltiplos caminhos (vence a maior propagação), trigger por exposição e pruning. Puro, sem banco (ver 2.9);
- **Hook de score do raio (6 casos novos, 94→100):** `BlastRadiusScoreProjectorTests` — penalização graduada (Crítico→NonCompliant, Alto→parcial), o gate severidade×alcance e a fonte Telemetry, com fake `IControlStateWriter` (sem banco/catálogo — o catálogo de teste não semeia ID.RA).
- **Telemetria de identidade do Entra ID (4 casos novos, 100→104):** `TelemetryIngestionServiceTests` — PR.AA reprova por privilegiados sem MFA; GV.RR reprova por excesso de admins (>10); a **discriminação por controle** (o MESMO retrato dá PR.AA `Compliant` + GV.RR `NonCompliant`); e o **controle compensatório** (contas OT sem MFA + `HasNetworkIsolation` → `MitigatedByThirdParty` 50%). Exercitam o elo `IdentityTelemetrySignal.ToMetricLines()` → motor → Stub pelo fluxo real.
- **Data/Platform/Network do Protect (6 casos novos, 104→110):** `TelemetryIngestionServiceTests` — reprova/aprova de PR.DS-01 (cripto < 95%), PR.PS-01 (patch crítico pendente) e PR.IR-01 (sem default-deny), construindo os sinais via os NOVOS records tipados (`Data/Platform/NetworkTelemetrySignal.ToMetricLines()`, ver 2.11). **Fecham a lacuna**: as 3 regras já existiam no `EvaluateProtect` mas nunca tinham teste de ingestão.

O catálogo de teste semeia PR.AA-01, **PR.DS-01 (peso 20), PR.PS-01 (peso 15), PR.IR-01 (peso 15)**, DE.CM-01, RS.MA-01, RS.MI-01, RC.RP-01, ID.AM-01, **GV.SC-01 (peso 15) e GV.RR-01 (peso 5)**. *(O Agentic Routing (2.8) AGORA tem cobertura dedicada — os dois arquivos acima; os testes de assessment não tocam banco: são roteamento puro + transporte fake.)*

### 2.6 Govern — dedupe de documentos é invariante de banco (sessão do índice único)

O índice `(TenantId, Sha256)` de `GovernanceDocument` era **não único** — os dois caminhos de ingestão (`GovernanceDocumentsController.Upload` e `PolicyIngestionWorker.SyncTenantAsync`) faziam `AnyAsync` → `Add` (read-then-write), então uma corrida (dois uploads simultâneos, ou o ciclo periódico do worker batendo com o `/sync` sob demanda no mesmo tenant) podia gravar o mesmo documento duas vezes. O worker mitigava com um `SemaphoreSlim _syncGate` 1×1 — paliativo em memória, inútil entre processos.

**Correção — idempotência virou invariante do banco:**
- `AegisScoreDbContext.OnModelCreating`: índice agora `.IsUnique()`, **parcial** (`WHERE "Sha256" IS NOT NULL` — o caminho `/connect` registra o documento antes de anexar o binário, então vários registros sem hash convivem).
- `Upload` e `SyncTenantAsync`: o `AnyAsync` prévio é só fast-path; o `SaveChangesAsync` do insert ganhou `catch (DbUpdateException)` idempotente (mesmo padrão de `AegisScoreSnapshotWorker`) — no `Upload` vira 409; no worker, `Detach` da entidade + log + `continue`.
- **`SemaphoreSlim _syncGate`/`GuardedSyncAsync` REMOVIDOS** do `PolicyIngestionWorker` — o banco impõe o dedupe e tenants distintos sincronizam em paralelo.
- Migration `20260711222804_UniqueGovernanceDocumentSha256`: dedupe defensivo de linhas pré-existentes (`ROW_NUMBER() OVER (PARTITION BY TenantId, Sha256 …)`) antes do `CREATE UNIQUE INDEX`. Aplicada ao banco de dev nesta arco de sessões.

### 2.7 Govern — telemetria estruturada (GV.SC/GV.RR) + Provider Pattern de ingestão

**Telemetria de governança (não só PDFs):** o pilar Govern ganhou avaliação por telemetria AUTORITATIVA, além da documental.
- DTOs de API `SupplyChainTelemetryDto` / `RolesTelemetryDto`; rotas `POST /telemetry/govern/{supply-chain,roles}` — uma expressão cada, reusando o seam `IngestCategory("Govern", …)`. Sem novos records na Application (reusa `CategoryTelemetrySignal`).
- Heurísticas `EvaluateGovern` no `StubLlmClient`: **GV.SC** reprova se `SuppliersWithNetworkAccess > 0 && !ThirdPartyAudited`; **GV.RR** reprova se `AdminAccountsWithoutReview > 0 || !PrivilegedAccessReviewConfigured`.
- Semântica: GV.SC/GV.RR via telemetria são autoritativos (podem chegar a 100%) e sobrescrevem o teto documental de 50% — mesma regra de ouro. GV.RR-01 pode ter as duas fontes (documental via PDF + telemetria).

**Provider Pattern de ingestão de documentos (anti vendor lock-in):**
- Porta `IDocumentIntegrationProvider` (`Task<IEnumerable<DocumentDto>> FetchPoliciesAsync(Guid tenantId, CancellationToken)`) + `IDocumentIntegrationFactory` (Application).
- `SharePointProvider` (Connectors.Microsoft) — **STUB** (`Provider => ConnectorProvider.Microsoft`, devolve políticas mockadas com conteúdo textual real; ainda NÃO chama Graph).
- `DocumentIntegrationFactory` (Infrastructure) — resolve a estratégia por `ConnectorProvider` a partir das estratégias registradas na DI (mesmo idioma do `ConnectorRegistry`).
- `PolicyIngestionWorker` (Api) — **fetch agnóstico**: dois disparos (timer periódico + gatilho `/sync` via `IPolicySyncTrigger`), enumera tenants com `ConnectorConfig` de capability `PolicyDocuments`, resolve o provider e materializa cada `DocumentDto` num `GovernanceDocument (Source=Integracao)` → storage → fila → `DocumentAnalysisWorker` (mesmo pipeline documental, teto 50%). Distinto do worker de ANÁLISE (que já era agnóstico).

### 2.8 Copiloto GRC onipresente + Agentic Routing

`POST /api/v1/auditor/chat` (`AuditorController`, `[Authorize]`, tenant do JWT nunca do corpo). Delega a `IAiAssessmentService.ChatAsync` (implementado em `ClaudeAssessmentService` e `StubAssessmentService`).

- **Consciência de contexto:** o corpo traz `ContextScope` (código da tela ativa: `GLOBAL`/`GV`/`ID`/`PR`/`DE`/`RS`/`RC`), mapeado no enum `AuditorScope` (`AuditorScopes.FromCode`, desconhecido → GLOBAL, fail-safe — o chat é read-only, não é fronteira de segurança). O **System Prompt é ajustado dinamicamente** por escopo (`ScopeFocus`): em PR exige métricas de MFA/criptografia; em GLOBAL age como gerador de relatório executivo do Secure Score; etc.
- **Agentic Routing (saída estruturada):** o prompt obriga a IA a devolver JSON `{intent, message, targetSubcategoryCode}`. `AuditorReply(Message, Scope, Intent, object? Metadata)`; enum `AuditorIntent {Copilot, StartInterview}`:
  - `COPILOT` — dúvida geral, respondida no `message`.
  - `START_INTERVIEW` — o usuário pediu para auditar/fechar lacunas → `message` JÁ É a 1ª pergunta do fluxo NIST e o `Metadata` (`AuditorInterviewSeed`) carrega o `targetSubcategoryCode`.
  - `ClaudeAssessmentService.ChatAsync` usa `ParseRouter` **resiliente** (JSON malformado → trata a conclusão inteira como COPILOT; o chat nunca quebra por formatação). `StubAssessmentService.ChatAsync` roteia por **palavra-chave** (auditar/diagnóstic/lacuna/gap/… → START_INTERVIEW, com 1ª pergunta canned por escopo).
- DTO de saída `AuditorChatResponseDto(Reply, Scope, Intent, object? Metadata)`.

### 2.9 Identify (ID.RA) — Raio de Explosão (topologia + motor)

Nova fatia do pilar **Identify**, complementar ao ID.AM (inventário): cruza **criticidade × ameaças reais × dependências** para medir o impacto de comprometer um ativo. **COMPLETO e VALIDADO AO VIVO ponta a ponta** (smoke test E2E — ver fim desta seção): domínio → motor puro → orquestrador → EF → HTTP → hook no ledger → frontend. Ancorada em ID.RA-03 (ameaças), ID.RA-04 (impacto) e ID.RA-05 (priorização).

- **Entidades (Domain, `BlastRadius.cs` + edits em `Tenancy.cs`/`Risks.cs`):**
  - `AssetDependency` — aresta DIRECIONADA do grafo (`Source` DEPENDE DE `Target`), com `DependencyType` + `DependencyStrength` (Hard/Soft/Redundant). É o grafo que faltava (ID.AM só ligava Asset→Processo).
  - `Threat` (reference data, `TenantId?` nulo = global, idioma do `IcrWeightProfile`) + `AssetThreatExposure` (ativo↔ameaça; `Likelihood` 1–4; `MitigatingSubcategoryCode`). Substituem as strings livres de `Risk.Threats`.
  - `BlastRadiusAssessment` + `BlastRadiusImpactNode` (1:N) — o snapshot explicável, com nós MATERIALIZADOS (leitura O(1), pruning de impacto baixo).
  - `Asset.BusinessImpact` — Owned VO `BusinessImpactProfile` (CIA + Fin/Op/Reg/Rep 1–4; RTO/RPO). `Risk.OriginExposureId` — gancho de promoção de exposição → risco.
- **Motor (Application `RiskAssessment/BlastRadiusCalculator.cs`):** `IBlastRadiusCalculator` puro/stateless (idioma dos `*ScoringService`).
  - **Dijkstra maximizante** sobre o grafo REVERSO (o raio de um ativo comprometido = quem depende dele, transitivamente). Decaimento multiplicativo `Hard 1.0 · Soft 0.5 · Redundant 0.25` (`DecayProfile`, injetável).
  - **Terminação garantida via `settled` set:** cada ativo finaliza no máx. 1×; a volta de um ciclo A→B→A é descartada na hora. **Múltiplos caminhos** → vence o de MAIOR propagação (produto de fatores ≤ 1 ⇒ extrair o maior primeiro é ótimo).
  - **Agregação Noisy-OR** `1 − Π(1 − dᵢ)` (satura em 100; cresce com severidade E alcance) × verossimilhança do gatilho (das exposições ativas do root; sem exposição → 1.0 = raio hipotético). Banda na régua 40/60/80 do `IcrScoringService`.
- **Orquestrador (Infra `RiskAssessment/BlastRadiusAssessmentService.cs`):** `IBlastRadiusAssessmentService` — carrega o grafo do tenant (query filter fail-closed), chama o motor puro, materializa `BlastRadiusAssessment` + nós e persiste (stamping automático do `SaveChanges`).
- **EF (`AegisScoreDbContext`):** DbSets das 5 entidades; `AssetDependency` com índice único `(TenantId, Source, Target, Type)`, check `Source <> Target` e **FKs `Restrict`** (2 FKs para Asset — evita cascade múltiplo no PG); `Threat` único `(TenantId, Code, Source)`; `AssetThreatExposure` único `(TenantId, AssetId, ThreatId)`; `BlastRadiusAssessment`→`ImpactedNode` **Cascade**. ✅ **Migration `Identify_BlastRadius` gerada e aplicada** no `aegis_dev`.
- **API (`BlastRadiusController`, `api/v1/risk-assessment`):** `POST /{assetId}/blast-radius` (`[Authorize]`, tenant implícito) — corpo opcional `{scenarioThreatId}` (`EmptyBodyBehavior.Allow`), invoca `AssessAsync`, devolve **201** com `BlastRadiusResponseDto` (score, nível, nós impactados) ou **404** se o ativo não existe no tenant.
- **Hook de score (`BlastRadiusScoreProjector`, Infra + `IBlastRadiusScoreProjector`):** o orquestrador o chama após persistir. Quando o raio é ALTO/CRÍTICO **e** amplo (≥ 5 ativos), penaliza **ID.RA-01/05** no `TenantControlState` via `ControlStateWriter` — fonte **`Telemetry`** (autoritativa, rebaixa; o raio deriva do estado técnico real), Crítico→`NonCompliant`, Alto→`MitigatedByThirdParty` (50%). O raio "dói" na nota NIST. **Assimétrico** (só penaliza — um raio menor não credita; a recuperação vem de evidência positiva de ID.RA). Sem tocar o enum `VerdictSource` (evita migration).
- **Owned VO — exceção EF única:** `Asset.BusinessImpact` OBRIGA `e.OwnsOne(a => a.BusinessImpact)` no DbContext (senão o EF invalida o modelo inteiro por PK ausente) — o Domain é POCO puro, então a config vive na Infra.
- **Testes:** `BlastRadiusCalculatorTests` (9 casos: linear, decaimento Soft/Redundant, decaimento composto, ciclo A↔B, ciclo com root, múltiplos caminhos, trigger, raio hipotético, pruning) + `BlastRadiusScoreProjectorTests` (6 casos do hook — ver 2.5).
- **Seed de topologia (`DevController.seed-demo`):** cria a demo do raio — ativo-raiz FIXO `DemoRootAssetId = bb000000-…-01` (o AD DC), **6 `AssetDependency`** em estrela (4 diretas Hard + 2 a 2 saltos via Soft: Notebook→VPN, SOC→M365), a `Threat` **T1486** (Ransomware, `KnownExploited`) e a `AssetThreatExposure` crítica (`Likelihood=4`) no root. O `WipeExistingDemoAsync` limpa as 5 entidades ID.RA ANTES dos Assets (FKs Restrict). Idempotente.
- ✅ **VALIDADO AO VIVO (smoke test E2E no `aegis_dev`, 2026-07-13):** `seed-demo` → login JWT → `POST /risk-assessment/{root}/blast-radius` devolveu a **degradação matemática exata** — d1 Hard = intrínseco integral (100/100/75/75), d2 Soft = ×0.5 (37.5/25), score **100 / Crítico**, 6 ativos, profundidade 2. O hook penalizou **ID.RA-01 e ID.RA-05 → `NonCompliant` 0/15, fonte `Telemetry`** no `TenantControlState` (confirmado em `/scoring/dashboard`). O raio "doeu" na nota: −30 pts de postura.

### 2.10 Telemetria de Identidade — Microsoft Entra ID (superfície ATIVA + controle compensatório OT)

Superfície de telemetria **ATIVA** (o Aegis PUXA os dados; ≠ webhooks passivos do 2.3), inspirada em frameworks de assessment de identidade (Purple Knight). **COMPLETA e VALIDADA AO VIVO ponta a ponta** (ver fim). Atua em Application, Connectors.Microsoft, Api + `StubLlmClient`.

- **Contrato multi-controle (Application/Telemetry/Models):** `IdentityTelemetrySignal` — retrato TIPADO de postura de identidade. Diferente do `CategoryTelemetrySignal` (1 controle), é **MULTI-CONTROLE**: o MESMO retrato alimenta PR.AA-01 (dimensão MFA) **e** GV.RR-01 (governança/excesso de admins), então NÃO carrega `SubcategoryCode` — o alvo é atribuído na ingestão. Campos: `TotalPrivilegedAccounts`, `PrivilegedAccountsWithoutMfa`, `PrivilegedAccountsWithMailbox`, `InactiveGuestAccountsOver30Days`, `MfaExemptServiceAccounts` (lista OT) + contexto de rede `HasNetworkIsolation` (bool) e `CompensatingControls` (tags). `ToMetricLines()` produz os rótulos canônicos (contrato de fio que o Stub lê), incl. `"Compensating Control: Network Isolation = True/False"`.
- **Porta (Application/Telemetry/Providers):** `IEntraIdTelemetryProvider.FetchIdentityPostureAsync(tenantId, tenantDomain, ct)` → DTO `EntraIdIdentityPosture` (leitura crua + `ToTelemetrySignal(hasNetworkIsolation, compensatingControls)` que ENXERTA o contexto de rede — o Entra não o conhece). ⚠️ **Decisão Principal:** a porta vive na **Application**, NÃO em Connectors.Microsoft (regra de dependência da Clean Architecture — mesmo padrão `IDocumentIntegrationProvider`/`SharePointProvider`).
- **Stub (Connectors.Microsoft):** `EntraIdTelemetryProviderStub` — cenário de ALTO RISCO fiel ao relatório `vicunha.com.br` (15 admins, 4 sem MFA, 9 com mailbox, 6 guests inativos, 3 contas OT). Registrado em `AddMicrosoftConnectors` (DI). Real (backlog): OAuth client credentials + Graph (directoryRoles / authenticationMethods / signInActivity).
- **Controller (Api):** `IdentityTelemetryController` → **`POST /api/v1/telemetry/identity/entra-id`** (`[Authorize]`, tenant do JWT via `ITenantContext`, 401 se ausente). Corpo OPCIONAL `EntraIdIdentityIngestionRequest(TenantDomain?, HasNetworkIsolation, CompensatingControls?)` — só o CONTEXTO de rede sobe do cliente; as métricas vêm do provider. **REUSA a esteira** `ITelemetryIngestionService.IngestCategoryAsync` (SEM serviço novo): 1 payload por controle (PR.AA-01, GV.RR-01), devolve `IReadOnlyList<TelemetryVerdictDto>`.
- **Heurísticas no `StubLlmClient` (ver 3):** discriminação por controle via `TargetsControl(p, "pr.aa"|"gv.rr")` (lê `subcategory: {code}` do User Prompt). **GV.RR-01** reprova se `total privileged accounts: > 10`. **PR.AA-01 é de TRÊS VIAS** (controle compensatório OT/IoT): `0 sem MFA` → Compliant; `sem-MFA + mfa-exempt service accounts > 0 + network isolation = true` → **`MitigatedByThirdParty`** (perdoa o falso positivo industrial); senão → `NonCompliant`. *(No motor real, exigir `privWithoutMfa <= isentas` p/ não mascarar admin HUMANO atrás do isolamento OT — backlog.)*
- ✅ **VALIDADO AO VIVO (smoke E2E, 2026-07-13):** `POST /telemetry/identity/entra-id` **sem** isolamento → PR.AA-01 e GV.RR-01 `NonCompliant` (0%); **com** `hasNetworkIsolation:true` → PR.AA-01 **`MitigatedByThirdParty` (10/20, 50%**, "falso positivo industrial evitado") e GV.RR-01 segue `NonCompliant` (15>10 — isolamento não perdoa excesso de admins). A discriminação por controle funcionou ponta a ponta.

### 2.11 Sinais tipados das demais categorias do Protect (PR.DS/PR.PS/PR.IR)

Paridade do padrão tipado (Application/Telemetry/Models) com o `IdentityTelemetrySignal`, agora para Data/Platform/Network:
- `DataTelemetrySignal(double EndpointEncryptionCoverage, bool UnencryptedTrafficDetected)`, `PlatformTelemetrySignal(double CisBenchmarkComplianceRate, int MissingCriticalPatchesCount)`, `NetworkTelemetrySignal(bool DefaultDenyFirewallEnforced)` — cada um com `ToMetricLines()` que produz **os MESMOS rótulos** que o `TelemetryController` compõe inline nas rotas `/protect/{data,platform,network}` e que o `EvaluateProtect` já lê.
- ⚠️ **As 3 REGRAS JÁ EXISTIAM** no `EvaluateProtect` (PR.DS/PS/IR, ver 2.4 + seção 3) desde a consolidação inicial — **nada foi alterado no Stub**. Os signals dão a paridade tipada e fecharam a **lacuna de teste** (6 casos, ver 2.5). As rotas ainda consomem os DTOs da Api (`DataProtectTelemetryDto` etc., mais ricos: Dlp/AppLocker/Microsegmentation); unificá-las para os signals é opcional (backlog).

---

## 3. Dicionário de Heurísticas (Tolerância Zero)

Regra de negócio implacável de cada categoria no `StubLlmClient`. **Reprova (`NonCompliant`) se qualquer condição for verdadeira**; caso contrário `Compliant`.

| Controle | Categoria | Reprova (NonCompliant) se |
|---|---|---|
| **ID.AM-01** | Asset (Identify) | `EdrCoverage == Absent` **ou** `OsLifecycle == EndOfLife` — *(EDR Active + 0 CVE = Compliant; intermediário = MitigatedByThirdParty 50%)* |
| **PR.AA-01** | Identity | `PrivilegedMfaCoverage < 100` **ou** `!ConditionalAccessEnforced` — *privilégio sem MFA é falha crítica* |
| **PR.AA-01** *(Entra ID, ver 2.10)* | Identity Posture | **3 vias:** `PrivilegedAccountsWithoutMfa > 0` **e** `MfaExemptServiceAccounts > 0` **e** `HasNetworkIsolation` → `MitigatedByThirdParty` (compensatório OT/IoT); só sem-MFA sem compensação → `NonCompliant`; 0 sem-MFA → `Compliant` |
| **PR.DS-01** | Data | `EndpointEncryptionCoverage < 95` **ou** `UnencryptedTrafficDetected` |
| **PR.PS-01** | Platform | `CisBenchmarkComplianceRate < 80` **ou** `MissingCriticalPatchesCount > 0` |
| **PR.IR-01** | Network | `!DefaultDenyFirewallEnforced` |
| **DE.AE-02** | Anomalies | `UninvestigatedHighAnomaliesCount > 0` **ou** `FalsePositiveRate > 50` — *fadiga de alerta* |
| **DE.CM-01** | Monitoring | `CriticalLogSourceCoverage < 95` **ou** `UnmonitoredCriticalAssetsCount > 0` — *ativo crítico cego* |
| **DE.AE-06** | Detection Eng. | `MitreAttckCoverageRate < 40` **ou** `SimulatedAttacksDetectedRate < 80` |
| **RS.MA-01** | Incident Analysis | `MeanTimeToAcknowledgeMins > 30` **ou** `ThreatHuntingCoverageRate < 80` |
| **RS.MI-01** | Incident Mitigation | `!AutomatedIsolationEnabled` **ou** `MeanTimeToRespondMins > 120` |
| **RC.RP-01** | Recovery Plan | `!ImmutableBackupsEnabled` **ou** `BackupIntegrityStatus != "Valid"` **ou** `!RecoveryTimeObjectiveMet` — *resiliência a ransomware* |
| **GV.SC-01** | Supply Chain | `SuppliersWithNetworkAccess > 0` **e** `!ThirdPartyAudited` — *fornecedor com acesso à rede sem auditoria de terceiros* |
| **GV.RR-01** | Roles & Auth. | `AdminAccountsWithoutReview > 0` **ou** `!PrivilegedAccessReviewConfigured` — *autoridade sem accountability* |
| **GV.RR-01** *(Entra ID, ver 2.10)* | Identity Governance | `TotalPrivilegedAccounts > 10` — *excesso de administradores quebra o menor privilégio; o isolamento de rede NÃO perdoa* |

*(GV.PO segue avaliado por documento — PDF → teto 50%. GV.SC/GV.RR agora têm telemetria autoritativa, ver 2.7. **PR.AA-01 e GV.RR-01 ganharam variante ADICIONAL de identidade do Entra ID** — mesmo código NIST, rótulos e regras distintos, discriminados por `TargetsControl` — ver 2.10.)*

---

## 4. Estado do Frontend (Angular 19) — painéis VIVOS + Copiloto

Padrão do projeto: standalone + signals, gráficos 100% nativos (SVG/canvas, sem libs), tema HUD dual-neon (CSS vars globais), auth via interceptor (`X-Tenant` + Bearer injetados em toda chamada ao `apiBase` — os serviços NÃO repetem headers). `ng build` verde (1 warning de budget CSS pré-existente em `document-hub`; o do `auditor-chat` sumiu no refactor).

### 4.1 Painéis de pilar (Protect/Detect/Respond/Recover) — Smart/Dumb, DRY
- `models/scoring.models.ts`: `TenantControlStateDto` (espelha o backend, camelCase), `PillarKey` (`PR|DE|RS|RC|GV`), `PILLARS` (metadados), e as funções puras `toControlView`/`buildPillarView` (pct = SUM(pontos)/SUM(peso), contagens, ordenação **NonCompliant primeiro**).
- `services/scoring.service.ts`: `getDashboard()` + `getPillarControls(pillar)` (filtra por prefixo). Resiliente (`catchError` normaliza o erro).
- **Dumb** `components/scoring/`: `ScoreGaugeComponent` (anel SVG, cor por faixa: ≥80 cyan / ≥50 âmbar / <50 vermelho) e `ControlComplianceCardComponent` (lista expansível; NonCompliant vermelho/brilho, Compliant silencioso, Parcial âmbar; `input.required`, estado de expansão em signal local).
- **Smart** `pages/pillar-dashboard.component.ts`: orquestrador ÚNICO (input `pillar`, signals `loading/error/controls`, `computed view`). Os 4 componentes de rota (`protect/detect/respond/recover-dashboard`) são **wrappers de 1 linha** (`<app-pillar-dashboard [pillar]="'PR'" />`) — DRY.

### 4.2 Govern — Central de Governança (`document-hub.component.ts` refatorado)
- **Postura de Governança** (topo): reusa `ScoreGauge` + `ControlComplianceCard` via `ScoringService.getPillarControls('GV')` — mesma leitura dos pilares (por isso `'GV'` entrou em `PillarKey`/`PILLARS`).
- **Integração Corporativa**: botão "Sincronizar Políticas Corporativas" → `GovernanceService.syncPolicies()` (`POST /governance/documents/sync`), com máquina de estado `idle→loading→done/error` (202-aware) e recarga assíncrona da lista.
- **Hub preservado**: upload manual, filtros, tabela de documentos, reanalyze/excluir, strip de cobertura documental. Tudo signal-first.

### 4.3 Copiloto GRC (Auditor Virtual) — global + context-aware
- **Já era global** (montado no `app.component` dentro de `<app-drawer>`). ⚠️ **DESATUALIZADO desde 2026-07-18:** o gatilho deixou de morar no sidebar e o botão duplicado do `document-hub` foi REMOVIDO — hoje o acesso é pelo **FAB flutuante global** (ver 12.4).
- `AgentStateService` (root, signals): deriva o contexto da rota via `router.events`; `ROUTE_TO_CONTEXT` agora mapeia **todas** as Funções (governance/assets/protect/detect/respond/recover); expõe `contextScope` (`GLOBAL|GV|ID|PR|DE|RS|RC`) e `drawerTitle` reativo.
- `services/auditor.service.ts`: cliente de `POST /auditor/chat` (envia `contextScope`). `AuditorChatReply` agora carrega `intent` (`COPILOT|START_INTERVIEW`) e `metadata` (`AuditorInterviewSeed{targetSubcategoryCode}`).
- `components/auditor-chat.component.ts`: **Copiloto real + ROTEADOR VISUAL declarativo** — signals `chatHistory`/`isAnalyzing`, bolhas HUD (cyan usuário / magenta auditor), auto-scroll (`viewChild` + `effect`). O `chatHistory` é uma **união discriminada de 3 variantes** (`TextChatMessage | InterviewChatMessage | BlastRadiusChatMessage`) e o template roteia com **`@switch` nativo, SEM `ViewContainerRef`**: `@case('text')` → bolha; `@case('interview')` → `grc-question-card`; `@case('blast_radius')` → `blast-radius-graph` (ver 4.4). `priorHistory` filtra só as bolhas de texto (type-guard) antes de enviar ao backend.
- `components/grc-question-card.component.ts` **(NOVO — Generative UI)**: micro-componente smart e autocontido da entrevista GRC. Recebe a semente do roteamento (`input.required<AuditorInterviewSeed>()`) e conduz `start→answer→complete` contra o `/governance/interviews` (antes órfão) via `GovernanceService`, publicando cobertura/risco no `AgentStateService` (barramento reverso — as telas de Govern reagem). Tema HUD magenta/brand. ✅ **A ENTREVISTA GRC ESTÁ REATIVADA NA UI.** Decisão de design: o `reply` do START_INTERVIEW é a abertura/transição; a entrevista persistida (fonte de verdade + outcomes) roda 100% pelo `/governance/interviews` — sem 1ª-pergunta duplicada.

### 4.4 Identify (ID.RA) — Generative UI do Raio de Explosão
- `components/blast-radius-graph.component.ts` **(NOVO — dumb, signal-first)**: recebe o DTO (`input.required<BlastRadiusResponse>()`) e renderiza o **Ativo Épico (root) em destaque** (score 0–100, nível de risco, contagens de ativos/processos/saltos) + a **tabela de nós** — id curto, distância (saltos), força do elo (Hard/Soft/Redundant) e impacto propagado numa barra normalizada pelo pico. Tema HUD: a moldura acende por severidade (Crítico vermelho…), elo Hard vermelho → Redundant cyan. Zero libs de grafo.
- `services/auditor.service.ts`: ganhou `assessBlastRadius(assetId, scenarioThreatId?)` (POST `/risk-assessment/{id}/blast-radius`) + os tipos `BlastRadiusResponse`/`BlastRadiusNode`.
- **Roteador de intenção NO FRONTEND:** ⚠️ o backend `/auditor/chat` ainda NÃO roteia BLAST_RADIUS (só COPILOT/START_INTERVIEW), e o raio é um endpoint SEPARADO que exige `assetId`. Então o `auditor-chat.send()` pré-roteia por palavra-chave (`/raio de explos|blast|topologia|…/`): detecta → extrai um UUID citado OU usa `environment.blastRadiusDemoAssetId` → chama `assessBlastRadius` → injeta a mensagem `blast_radius`; senão segue ao Copiloto. (Fechar o círculo "pela IA" = adicionar a intenção BLAST_RADIUS ao Agentic Routing do backend — backlog.)
- ✅ **VALIDADO AO VIVO:** login → Auditor → "raio de explosão" → o `@case('blast_radius')` injetou o gráfico com os dados reais do backend (`201` + tabela com a degradação 100/100/75/75/37.5/25). *(Corrigido em passagem: a barra de 100% empurrava o número para fora — trilha `flex:1` + número fixo à direita.)*

### 4.5 Correção — vazamento da casca na tela de Login
`app.component.ts` renderizava o sidebar em `/login` porque o `@if (auth.isAuthenticated())` ficava true (o token vive em memória e não é limpo ao cair no login), então o `<router-outlet>` do login herdava a casca. **Fix:** injetado o `Router`, signal `currentUrl` (reativo via `NavigationEnd`, padrão do `AgentStateService`) e `showShell = isAuthenticated() && !url.startsWith('/login')`; o template usa `@if (showShell())`. Defesa em profundidade — `/login` nunca mais herda o sidebar. (Nota: template é **inline** no `.ts`, não há `app.component.html`.) Validado ao vivo: em `/login` só o formulário aparece.

### 4.6 Postura de Identidade (Entra ID) — tela ATIVA + demo de compensação ao vivo
Rota **`/identity`** (`pages/identity-posture-dashboard.component.ts`, SMART, padrão PillarDashboard), no grupo de nav "Telemetria Ativa". Consome a esteira ATIVA `IdentityService.runEntraIdAnalysis()` (POST /telemetry/identity/entra-id).
- **Dumb components:** reusa `ScoreGaugeComponent` (gauge "POSTURA IAM"); `IdentityExposureCardComponent` (NOVO, `components/identity/` — tabela tática: Achado · Plataforma [◆ Entra / ⬢ AD / ◉ Okta] · Severidade · Status, linhas expansíveis com o `aiEvidence`); `SeverityComponent` (NOVO, `components/scoring/` — chip Critical…Informational com pips + cor HUD). Modelos puros em `models/identity.models.ts` (`buildIdentityPostureView`, `IdentityVerdict`/`IdentityFinding`).
- **Demo de compensação AO VIVO:** o toggle **"Rede Isolada (OT)"** reenvia `hasNetworkIsolation` e RE-AVALIA → PR.AA-01 migra `NonCompliant`→`MitigatedByThirdParty` com badge âmbar **"COMPENSATED CONTROL"** (severidade Crítico→Médio, gauge sobe); GV.RR-01 segue Não Conforme. É a prova visual de "a IA perdoa MFA se o ativo está isolado".
- **Integração Copiloto (barramento reverso NOVO):** botão **"Auditar Lacunas"** → `AgentStateService.requestAudit(prompt)` (signal `pendingPrompt` + `consumePendingPrompt()`; um `effect` no `auditor-chat` consome e envia como se digitado) → backend roteia `START_INTERVIEW` (targetSubcategoryCode PR.AA-01). ⚠️ A rota `/identity` mapeia para o contexto **Protect (PR)** em `ROUTE_TO_CONTEXT` — gestão de identidade/acesso é PR.AA no CSF 2.0, NÃO o pilar Identify (ID.AM/ID.RA).
- ✅ **VALIDADO AO VIVO (smoke E2E, 2026-07-13):** login → `/identity` → tabela popula (200 OK); toggle OFF→ON confirmado por rede (PR.AA 0%→50%, badge) e DOM; "Auditar Lacunas" → POST /auditor/chat intent START_INTERVIEW. Console limpo. *(A tabela mostra 1 linha por CONTROLE avaliado, não por IOE individual — enriquecer o response com findings por indicador é backlog; o modelo do frontend já suporta N.)*

### 4.7 Humanização das siglas NIST (glossário)
As siglas (ex.: `PR.AA-01`) são essenciais ao motor mas hostis na tela. Camada de tradução visual:
- `models/nist-glossary.ts` (NOVO): `NIST_CATEGORY_NAMES` (categoria → PT-BR, todas as famílias: "PR.AA"→"Identidade e Acesso", "PR.DS"→"Proteção de Dados", "PR.PS"→"Segurança de Plataforma", "PR.IR"→"Rede e Infraestrutura", + GV/ID/DE/RS/RC) + funções PURAS `categoryName(code)` / `friendlyControlLabel(code)`.
- `ControlComplianceCardComponent`: cada linha agora mostra o **nome amigável em destaque + o código** embaixo (reexpõe `categoryName` ao template; grid ajustado). `PILLARS` (scoring.models): blurbs dos pilares traduzidos para PT-BR (mesmos nomes do glossário — o subtítulo bate com as linhas).
- ✅ **VALIDADO AO VIVO (2026-07-13):** ingeri os 4 controles PR e `/protect` renderizou "Identidade e Acesso (PR.AA-01)", "Proteção de Dados (PR.DS-01)", "Segurança de Plataforma (PR.PS-01)", "Rede e Infraestrutura (PR.IR-01)" + blurb humanizado, ordenados NonCompliant primeiro. Console limpo. Reutilizável por todos os pilares.

---

## 5. Ponto de Parada e Backlog

**Onde paramos (2026-07-19, rodada mais recente):** ⭐ **CENTRAL DE INTEGRAÇÕES (§23).** Primeira UI dos conectores — até aqui o backend tinha o upsert mas **não havia por onde inserir credencial**. Exigiu abrir o `GET /api/v1/connectors` que faltava (tela de gerenciamento sem listagem fica em branco no F5) e trouxe `@angular/forms` ao projeto, que nunca o tivera. O segredo segue **escrita-apenas**: só o booleano `hasCredentials` sai. **219/219 testes**, `ng build` verde. ⚠️ Sem `DELETE`/toggle de `Enabled` e sem teste de frontend — ver §23.4.

**Onde paramos (2026-07-19, anterior):** ⭐ **SSO SIMULADO — LOGIN SEM SLUG + TENANT SWITCHER (§22).** A credencial subiu para a `IdentityAccount` GLOBAL (e-mail único no sistema) e o `User` virou MEMBERSHIP com FK para ela — o vínculo pessoa↔tenant deixou de ser coincidência de string e virou chave estrangeira. Isso fechou um **bypass de autenticação crítico** que o enunciado literal teria criado (ver §22.1). Login por e-mail+senha, `GET /users/me/tenants` e `POST /auth/switch-tenant` (ancorados na claim NOVA `account_id`); no frontend o `X-Tenant` passou a ser derivado do próprio token e o `environment.tenantId` foi removido. `TenantConsistencyMiddleware`, query filters das demais entidades e o `StampTenant` fail-closed **intocados**. **211/211 testes**, `ng build` verde, migration com backfill aplicada.

**Onde paramos (2026-07-19, anterior):** ⭐ **IDENTIDADES SOBRE A FUNDAÇÃO UM-PARA-MUITOS (§21).** `IUserManagementService` provisiona usuários e concede acesso SEMPRE dentro do tenant ambiente — sem leitura cross-tenant, sem `IgnoreQueryFilters`, sem bypass de `TenantId`, sem migration. Mesmo e-mail em dois tenants = duas identidades independentes (o índice único é `(TenantId, Email)`). `PlatformAdmin` **não é atribuível** por esta superfície, nos dois caminhos (create e update). Política de senha NIST SP 800-63B: 12–128 chars, sem regra de composição. `UsersController` novo e separado do `AuthController` anônimo. **210/210 testes.** ⚠️ A flag `IsGlobalAdmin` do enunciado foi **abortada por decisão do Felipe** — ver §21.1.

**Onde paramos (2026-07-19, anterior):** ⭐ **ONBOARDING CONSOLIDADO E IDOR FECHADO (§20).** O `TenantsController` deixou de ser um script de persistência: normalização/unicidade de slug, cifragem estática de credenciais e vínculo ao tenant correto agora têm dono único no `ITenantManagementService` (porta na Application, adapter na Infrastructure). O `ConnectorsController`, que tinha **`tenantId` de rota sem função de autorização** (IDOR latente — ⚠️ NÃO era anônimo, ver a correção na §20.2), virou `api/v1/connectors/{id}` sob `[Authorize]` explícito. O upsert de conector passou a ser invariante de BANCO (índice único `(TenantId, Provider, Capability)`), com reconvergência para UPDATE na corrida perdida. **187/187 testes**, build verde. ⚠️ **A migration está GERADA mas NÃO aplicada** — ver item 0 abaixo.

**Próxima etapa:** a definir (a etapa de Usuários fechou na §21).

**Onde paramos (2026-07-18):** ⭐ **O AUDITOR GANHOU PERSONA, VOCABULÁRIO DE LACUNAS E BLINDAGEM.** Seis frentes fechadas — **165/165 testes**, `ng build` e build .NET verdes:

- **§13 Persona** — `AuditorPersonality.json` + provider singleton; System Prompt em 3 blocos (rubrica → persona → contrato). Alcança telemetria **e** o caminho documental.
- **§14 MissingRequirements** — o ledger passou a distinguir **falta de telemetria × falta de documentação**, tipado do domínio (`jsonb`) até o ícone (rede × pasta) na UI. `RuleEvaluator` é o motor único da distinção.
- **§15 TTL de sinal** — `DefaultSignalFreshnessHours: 72`; sinal velho vira ponto cego na LEITURA. Cobertura documental só conta se **aceita pelo RAG**.
- **§16 RAG documental de 2 passadas** — triagem → julgamento dirigido com a regra do 800-53 e trecho selecionado (`DocumentChunker`). Regras **GV.PO-01/GV.RR-01** criadas (97 → 99) e seeder tornado **incremental**.
- **§17 Resiliência** — Polly v8 (retry exponencial + circuit breaker + timeout) nos HttpClients de IA; shutdown gracioso e recuperação de fila no worker.
- **§18 Auditoria do Executivo** — campos órfãos ligados, estado vazio elegante, escala do gráfico separada da métrica.
- **§19 Valor C-Level** — as 3 métricas "esquecidas" que a §18.1 apontou foram IMPLEMENTADAS: tendência (a derivada do risco), balanço CAPEX × OPEX das lacunas e o custo do fracasso (raio de explosão).

**Onde paramos (rodada anterior):** ⭐ **MOTOR RAG VALIDADO CONTRA A API REAL** — o Gemini lê a regra `jsonb`, executa a rubrica matemática (fail-closed), preenche o `ControlIntelligence` completo e recusa alucinar quando a evidência não existe (**seção 11**). O ledger tem inteligência real gravada, e por isso o mock do frontend foi **deletado** (10.4). Modelo default agora `gemini-flash-latest` (11.4). **UI/UX polida** (seção 12): FAB global da égide substituiu o gatilho do sidebar e o botão duplicado do Govern; rolagem do sidebar no idioma Synapse; seção "Referência" com o link do NIST; e a tabela de ativos alinhada ao desfazer a colisão com o utilitário global `.grid`. Abaixo, o histórico anterior:

**Onde paramos (anterior):** backend NIST completo; **110/110 testes**. **Pilar Protect FECHADO**: PR.AA (identidade Entra + compensação OT) e PR.DS/PR.PS/PR.IR com **sinais tipados** na Application (ver 2.11) e **cobertura de teste** — ⚠️ as 3 regras PR.DS/PS/IR já existiam no `EvaluateProtect` (nada mudou no Stub). **Ingestão de Identidade do Entra ID — COMPLETA e VALIDADA AO VIVO ponta a ponta** (ver 2.10 + 4.6): contratos multi-controle → provider stub → `IdentityTelemetryController` (POST /telemetry/identity/entra-id, reusa a esteira) → **controle compensatório OT** (3 vias em PR.AA-01) → tela HUD `/identity` com toggle de isolamento (demo ao vivo: NonCompliant→Mitigado + badge "COMPENSATED CONTROL") + "Auditar Lacunas" → Copiloto (START_INTERVIEW). **ID.RA (Raio de Explosão) — COMPLETO e VALIDADO AO VIVO** (motor Dijkstra → hook no ledger → Generative UI `blast-radius-graph`, ver 2.9/4.4). **Humanização das siglas NIST** (glossário PT-BR no card + pilares, ver 4.7). Provider Pattern documental + `/sync` prontos (SharePoint STUB). Frontend: 4 painéis de pilar + Govern + Copiloto (Agentic Routing + entrevista GRC) + tela de Identidade; a casca não vaza no `/login` (ver 4.5). ⚠️ **Commitado em `main`** até `3fd88d7` — a afirmação "nada commitado" desta seção venceu; ver o cabeçalho e a seção 10.

**Próximos passos imediatos (prioridade):**

1. ~~**ID.RA — Raio de Explosão completo ponta a ponta**~~ ✅ **CONCLUÍDO e VALIDADO AO VIVO** — motor + hook + migration + endpoint + **seed de topologia** + **Generative UI** (`blast-radius-graph`, ver 4.4); smoke test E2E ok (ver 2.9). **Refinamentos futuros:** (a) adicionar a intenção **BLAST_RADIUS ao Agentic Routing** do backend (hoje o gatilho é frontend por palavra-chave); (b) **enriquecer o `BlastRadiusResponseDto`** com o NOME dos ativos (a UI mostra Guids curtos); (c) limiares do hook (severidade/alcance) configuráveis via `RiskAppetite`.
1. ~~**Reativar a Entrevista GRC no Angular via Agentic Routing**~~ ✅ **CONCLUÍDO** (ver 2.5 + 4.3) — o `auditor-chat` trata `Intent === "START_INTERVIEW"` via união discriminada + `@switch` e injeta o `grc-question-card`, que consome `/governance/interviews` (start/answer/outcomes) e publica no barramento. *(Pendência fina: os outcomes ainda saem do `StubAssessmentService.SuggestMaturity` canned — ligar ao motor real cai no item 3.)*
1. ~~**Ingestão de Identidade do Entra ID (telemetria multi-controle + compensação OT + tela HUD)**~~ ✅ **CONCLUÍDO e VALIDADO AO VIVO** (ver 2.10 + 4.6). **Refinamentos futuros:** (a) `EntraIdTelemetryProviderStub` → OAuth client credentials + Microsoft Graph real; (b) no motor real, exigir `PrivilegedAccountsWithoutMfa <= MfaExemptServiceAccounts` p/ não mascarar admin HUMANO atrás do isolamento OT; (c) **enriquecer o response com findings por INDICADOR** (hoje a tabela mostra 1 linha por controle NIST — o modelo do frontend já suporta N).
1. ~~**Fechar o Protect (PR.DS/PS/IR) + humanizar as siglas NIST**~~ ✅ **CONCLUÍDO e VALIDADO AO VIVO** (ver 2.11 + 4.7): sinais tipados Data/Platform/Network + 6 testes (lacuna fechada) + glossário PT-BR no card/pilares. **Refinamento opcional:** unificar as rotas `/protect/{data,platform,network}` para consumirem os signals (hoje usam os DTOs da Api mais ricos — Dlp/AppLocker/Microsegmentation), só se quisermos eliminar a composição inline.
2. **Conectores Microsoft ainda são STUB** — `SharePointProvider.cs` (documentos, falta OAuth client credentials + `GET /sites/{id}/drive/root/children`) **e** `EntraIdTelemetryProviderStub.cs` (identidade, falta OAuth + Graph directoryRoles/authenticationMethods/signInActivity). Ambos com o Provider Pattern JÁ pronto; segredos em `ConnectorConfig.EncryptedSettings`.
3. **Ligar o motor real de IA** — ✅ **O `GeminiLlmClient` ESTÁ LIGADO E VALIDADO** (seção 11): o 429 de 2026-07-13 era **cota por modelo**, não chave inválida; o default virou `gemini-flash-latest` e o motor avalia de verdade (lê o jsonb, calcula a rubrica, preenche o `ControlIntelligence`). **Falta ainda o `ClaudeAssessmentService`** (Anthropic, `claude-sonnet-5`) — o Copiloto/Advisories seguem no `StubAssessmentService`. Para FORÇAR os Stubs numa prova E2E, subir a API com `AegisAi__ApiKey=' '`/`Ai__ApiKey=' '` (ESPAÇO: no PowerShell `''` REMOVE a env var; um espaço passa `IsNullOrWhiteSpace`=true → o DI cai no Stub). O Copiloto GLOBAL ainda não injeta a postura real do tenant no prompt (hoje só persona por escopo) — hook para as queries de scoring.
4. **HUD `/trend`** só preenche via `AegisScoreSnapshotWorker` (foto à meia-noite UTC); considerar snapshot no boot em DEV.
5. **Trend no Dashboard Executivo** (ver 18.1) — a série já existe e o `SparklineComponent` também; falta o encanamento. É o maior ganho por esforço da lista.
6. **9 subcategorias ainda sem regra** no `aegis_assessment_rules.json` (99 de 106). Sem regra, o RAG documental cai no fallback de triagem cega e o `RuleEvaluator` não classifica a natureza da lacuna. Priorizar as de maior tráfego.
7. **Embeddings para o `DocumentChunker`** (ver 16.2) — o casamento léxico chegou ao seu limite útil; coincidências como *"informação íntegra"* × *"Gerente de Segurança da Informação"* só se resolvem semanticamente.
8. **`EvidenceSignal` sem produtor real** (ver §15) — quando os conectores passarem a gravá-lo, o relógio do TTL vira `MAX(LastEvaluatedAt, CollectedAt)`.
9. **Mover os workers para `Infrastructure`** (ver 18.3) — hoje vivem em `AegisScore.Api`, fora do alcance da suíte de testes; são adaptadores, não composição.

**Ambiente de execução (DEV):** API em `http://localhost:5100` (`dotnet run --launch-profile http`). ⚠️ Banco real (via `dotnet user-secrets`) é **`aegis_dev`** — diverge do `Database=aegis` do `appsettings.json` versionado (DEV.md Passo 1). DemoTenant `aa000000-0000-0000-0000-000000000001`; usuário `analista@demo.aegis` / `Aegis@12345` (via `POST /dev/seed-user`). ⚠️ O **login (`POST /auth/login`) exige o header `X-Tenant`** — multi-tenant resolve o usuário dentro do tenant. Segredos (JWT, connection string, `AegisAi:ApiKey`, `Ai:ApiKey`) em `dotnet user-secrets` — ver `DEV.md`. **Schema aplicado no boot** (`db.Database.MigrateAsync()` em `Program.cs`) — não rodar `dotnet ef database update` à mão em uso normal. Frontend: `npm --prefix <frontend> run build` (ou `ng serve`); `environment.tenantId` = DemoTenant. ⚠️ Ao rodar `dotnet build` da solução com a API ativa, o `bin` do Api fica travado (MSB3026) — parar a API ou compilar o Api isolado (`--no-dependencies -o <tmp>`).

⚠️ **CORS (dev):** a política `aegis-spa` (`Program.cs`) só libera `http://localhost:{5173,5273,3000}` — a porta padrão 4200 é bloqueada. **`.claude/launch.json` (na RAIZ `C:\Projetos`, não em `AEGIS/`)** já define `aegis-backend` (5100) e `aegis-frontend` (**5273**) — usar esses nomes; não criar um segundo launch.json dentro de `AEGIS/`, que o harness não lê. **Raio de explosão demo:** `DevController.DemoRootAssetId = bb000000-0000-0000-0000-000000000001` (o AD DC), espelhado em `environment.blastRadiusDemoAssetId`. **Motor de IA real (trocar o Stub):** `dotnet user-secrets set "Ai:ApiKey" "sk-ant-…" --project src/AegisScore.Api` — chave Anthropic; o DI (`AddAegisScoreInfrastructure`) troca o `StubAssessmentService` pelo `ClaudeAssessmentService` (`claude-sonnet-5`) no próximo boot. O `StubLlmClient`/`GeminiLlmClient` seguem a MESMA lógica com `AegisAi:ApiKey` (modelo default `gemini-flash-latest` — ver 11.4). **Teste de fogo do RAG:** `POST /api/v1/dev/rag-fire-test?scenario=all` após `POST /api/v1/dev/seed-demo` (ver seção 11). **Demo de identidade (Entra ID):** a tela `/identity` foi validada em `ng serve --port 5273`; o toggle "Rede Isolada (OT)" re-avalia e demonstra a compensação ao vivo (PR.AA-01 NonCompliant→Mitigado).

---

## 6. Feature — Checklist Dinâmico (decomposição do veredito em checks ✓/✕)

**Objetivo:** cada card de controle vira um accordion que lista os itens técnicos (checks) que justificam o status. Ponta a ponta, validado em **2026-07-14** (Build + 111 testes verdes; migration aplicada ao `aegis_dev`).

- **Contrato (`Application/Telemetry/Models`):** `record ComplianceCheck(string Name, bool Passed, string Details)`. `ComplianceVerdict` ganhou `IReadOnlyList<ComplianceCheck> Checks { get; init; }` (default vazio). ⚠️ NÃO existe `ControlVerdict` — o veredito transitório é `ComplianceVerdict`.
- **Motor:** `StubLlmClient.BuildProtectChecks(p)` decompõe as métricas do Protect (PR.AA/DS/PS/IR) em checks por-condição (ex.: "Endpoint Encrypted" = cripto ≥ 95%); `ExecutePromptAsync` serializa `{status, aiEvidence, checks}` via `JsonSerializer` (era interpolação crua). `AegisAiEvaluatorService.ParseResponse` desserializa os checks e os repassa ao writer (o motor real Gemini ainda não os emite → lista vazia).
- **Persistência:** `TenantControlState.ChecksJson` (text, nullable) — migration **`20260714204623_AddChecksJsonToControlState`** (só `AddColumn`), aplicada ao `aegis_dev`. `IControlStateWriter.ApplyVerdictAsync` ganhou `IReadOnlyList<ComplianceCheck>? checks = null` ANTES do `ct` — os chamadores posicionais (`BlastRadiusScoreProjector`, `DocumentAnalysisWorker`) passaram a nomear `ct:`; `ControlStateWriter` serializa na escrita e desserializa no retorno recusado.
- **Leitura:** `ControlStateDashboardQuery` projeta o `ChecksJson` cru no banco (via `record Row`) e desserializa em memória — o EF não traduz JSON→objeto no SQL; `TenantControlStateDto` ganhou `IReadOnlyList<ComplianceCheck> Checks`. Serializa camelCase para o front (`{name, passed, details}`).
- **Frontend:** `ComplianceCheck` model + `TenantControlStateDto.checks`/`ControlView.checks` (`toControlView`); `ControlComplianceCardComponent` reusa o toggle de expansão existente e lista os checks no corpo do accordion — ícone `✓` (cyan) / `✕` (vermelho), nome + detalhe, HUD dual-neon.
- **Testes — 111/111 verdes** (110 → 111): `ControlStateDashboardQueryTests.GetDashboardAsync_DesserializaOChecklistTecnicoPersistido` fecha o loop persistência → leitura. Build da solução .NET + `ng build` verdes.
- **Escopo:** só o Protect emite checks hoje (via `BuildProtectChecks`); as demais famílias persistem checklist vazio. Estender = adicionar blocos análogos por família no Stub.


---

## 7. Fase 1 — Recomendações de Remediação (Advisories): Backend

**Objetivo:** transformar o diagnóstico (`TenantControlState`) em AÇÃO consultiva. O SOC gera uma recomendação técnica mastigada — **Risco Documentado** (o "porquê") + **Passo a Passo Técnico** exportável (o "como fazer") — endereçada a um controle NIST, para a TI do cliente executar e elevar o Secure Score. É motor CONSULTIVO, não ferramenta de execução de TI. Ponta a ponta em **2026-07-14** (Build da solução + **115/115 testes** verdes; migration aplicada ao `aegis_dev`).

- **Domínio (`Domain/Advisories.cs`):** entidade `RemediationAdvisory : Entity, ITenantOwned`. Campos: `SubcategoryCode` (string "PR.DS-01" — como `IdentifiedRisk`/`SubcategoryCoverage`, **sem FK ao catálogo**: desacoplado de versão de framework), `Title`, `DocumentedRisk` (o *RiscoDocumentado*), `TechnicalSteps` (o *PassoAPassoTecnico*). ⚠️ Propriedades em **inglês** (convenção do Domain); os nomes PT do enunciado viram `DocumentedRisk`/`TechnicalSteps`, mapeados no XML doc.
- **Application:**
  - `Advisories/GenerateAdvisory.cs`: `record GenerateAdvisoryCommand(string SubcategoryCode)` (só o código trafega — o texto vem do motor, nunca do cliente); `record RemediationAdvisoryDto(Id, SubcategoryCode, Title, DocumentedRisk, TechnicalSteps, CreatedAt)`; e a porta `IGenerateAdvisoryHandler.HandleAsync` (o padrão porta/adaptador das queries, agora do lado da ESCRITA).
  - `IAiAssessmentService` ganhou `GenerateAdvisoryAsync(AdvisoryGenerationRequest) → AdvisoryDraft(Title, DocumentedRisk, TechnicalSteps)` (records novos no `Abstractions.cs`).
- **Infraestrutura:**
  - `Advisories/GenerateAdvisoryHandler.cs` (scoped): a IA redige o rascunho → materializa `RemediationAdvisory` → `SaveChangesAsync` (o `StampTenant` carimba o `TenantId` fail-closed; o handler NUNCA atribui tenant). O texto é sempre do motor.
  - `StubAssessmentService.GenerateAdvisoryAsync`: banco canned por código (PR.AA-01/DS-01/PS-01/IR-01 — ex.: PR.AA-01 → "Impor MFA … via Conditional Access") + fallback genérico ancorado no código. `ClaudeAssessmentService`: prompt → JSON `{title, documentedRisk, technicalSteps}` (motor real com `Ai:ApiKey`).
  - DI: `services.AddScoped<IGenerateAdvisoryHandler, GenerateAdvisoryHandler>()`.
  - `AegisScoreDbContext`: `DbSet<RemediationAdvisory> RemediationAdvisories`; índice tenant-leading `(TenantId, SubcategoryCode)` (**não único** — admite histórico de revisões por controle); `SubcategoryCode` max 15, `Title` max 300; Global Query Filter fail-closed.
- **API:**
  - `AdvisoriesController` (`[Authorize]`, `[Route("api/v1/scoring/advisories")]`): `POST` → **201 Created** com `RemediationAdvisoryDto` (+ header `Location`); **400** se `subcategoryCode` vazio. Tenant IMPLÍCITO (claim do JWT); o `TenantConsistencyMiddleware` fail-closed segue barrando (403) token sem tenant válido ou divergente do `X-Tenant`.
  - DTO de entrada `CreateAdvisoryRequest(string SubcategoryCode)` (`Contracts/Dtos.cs`) — só o código; o texto é redigido no servidor (anti-injeção de prosa).
- **Migration `20260714214906_AddRemediationAdvisory`** (apenas `CreateTable` + o índice; sem drift — `has-pending-model-changes` limpo), **aplicada ao `aegis_dev`** via `dotnet ef database update` (ambiente Development → connection do user-secrets).
- **Testes — 115/115 verdes** (111 → 115): `GenerateAdvisoryHandlerTests` (SQLite in-memory + `StubAssessmentService` real) — persistência + texto canned do Stub, stamping do tenant (fail-closed), isolamento entre tenants (Global Query Filter, sem `Where`) e recusa sem tenant resolvido (`TenantSecurityException`).
- **Escopo/backlog:** só a CRIAÇÃO (POST). Faltam a LEITURA (`GET /advisories`, `GET /advisories/{id}`), a exportação do passo a passo e a frente Angular. O texto canned cobre só o Protect; estender = novas entradas no banco do Stub (ou ligar o `ClaudeAssessmentService` real).

---

## 8. Fase 2 — Recomendações de Remediação (Advisories): Frontend

**Objetivo:** o analista gera e visualiza uma recomendação consultiva direto de um controle **NÃO CONFORME** no card de conformidade (motor consultivo na UI). Build `ng` verde; contrato validado contra o Swagger da API viva (2026-07-14).

- **Modelos (`models/scoring.models.ts`):** `GenerateAdvisoryCommand { subcategoryCode }` (espelha `CreateAdvisoryRequest`) e `AdvisoryDto { id, subcategoryCode, title, documentedRisk, technicalSteps, createdAt }` (camelCase, espelha `RemediationAdvisoryDto`). ⚠️ Mapeamento camelCase conferido 1:1 contra o schema do `/swagger/v1/swagger.json` da API rodando.
- **Serviço (`services/advisory.service.ts` — NOVO):** `AdvisoryService.generate(subcategoryCode) → Observable<AdvisoryDto>` via `POST /api/v1/scoring/advisories`. Padrão do `ScoringService`: `HttpClient` + `environment.apiBase`, `catchError` normaliza o erro num `Error` limpo. **X-Tenant + Bearer são injetados pelo `authInterceptor`** (toda chamada ao `apiBase`) — o serviço NÃO repete headers. Dedicado (não no `ScoringService`) para separar a ESCRITA consultiva das queries read-only do HUD.
- **Componente (`components/scoring/control-compliance-card.component.ts`):** deixou de ser Dumb-puro — passa a injetar `AdvisoryService` (única dependência). Estado por-controle num Signal `Map<code, AdvisoryUiState>` (união discriminada `loading | loaded | error`; ocioso = ausência de chave), isolado por código para não vazar entre linhas. `generateAdvisory(code)` barra reentrância enquanto carrega.
- **Fluxo de UI (no corpo expandido do card, SÓ se `status === 'NonCompliant'`):** botão **"✦ Gerar Recomendação"** (tema HUD **magenta** = ação) → `loading` (spinner + pulse magenta) → `loaded`: card magenta com **Título**, **Risco Documentado** (`<p>`) e **Passo a Passo Técnico** (`<pre>` `white-space: pre-wrap`, preserva a numeração multilinha) + rodapé "Gerado em …" e **Regenerar**; ou `error` com **Tentar novamente**. Roteado por `@let adv = advisoryState(c.code)` + `@if`. Visualização inline dedicada (não modal) — minimalista, dual-neon.
- **Verificação:** `ng build` (AOT) verde — tipos + template (`@let`/`@if` da união discriminada) + wiring do serviço. Contrato request/response batendo com a API viva (Swagger). ⚠️ Avisos de budget de CSS não-fatais (`control-compliance-card` 5.47 kB, `document-hub`, `asset-inventory`) — consistentes com a postura do projeto; o build gera o bundle. **Pendente:** o click-through autenticado ao vivo (exige login — não digitado pela IA por política de credenciais).
- **Nota backend (polish):** o endpoint responde **201** em runtime, mas o Swagger lista só **200** (falta `[ProducesResponseType(201, typeof(RemediationAdvisoryDto))]` no `AdvisoriesController`). Não afeta o front (o `HttpClient.post<AdvisoryDto>` trata qualquer 2xx igual).
- **Escopo/backlog:** só o Protect tem controle NonCompliant na demo, então é onde o botão aparece hoje — mas qualquer pilar (via `ControlComplianceCardComponent`) o exibe em controle não-conforme, inclusive o Govern (reusa o mesmo card). Falta a LEITURA/histórico de advisories já gerados e a exportação do passo a passo.

---

## 9. Polimento da Sprint (DRY + Swagger)

Fechamento de dívidas técnicas — build .NET verde (0/0) + `ng build` verde (2026-07-14).

- **Backend (Swagger tipado):** o `POST` do `AdvisoriesController` ganhou `[ProducesResponseType(typeof(RemediationAdvisoryDto), Status201Created)]` + `[ProducesResponseType(Status400BadRequest)]`. O Swagger da API viva agora reflete o contrato real: respostas **201 (RemediationAdvisoryDto) + 400** — antes só listava 200 (o `Created(...)` não tipava sem o atributo). ⚠️ Usei a forma canônica `(Type, StatusCode)`; a ordem literal `(StatusCode, Type)` do enunciado não é um construtor válido de `ProducesResponseTypeAttribute`.
- **Frontend (DRY das descrições NIST):** criado o dicionário ÚNICO `NIST_FUNCTION_DESCRIPTIONS` (as 6 Funções GV/ID/PR/DE/RS/RC) + o tipo `NistFunctionCode` em **`models/nist-glossary.ts`** (a camada de humanização; sem imports → sem risco de ciclo). `PILLARS` (`scoring.models.ts`) passou a **derivar** `description` do dicionário — removidas as 5 descrições inline duplicadas; `pillar-dashboard` e `document-hub` consomem via `PILLARS`, sem hardcode. `asset-inventory` (`/assets`) passou a consumir `NIST_FUNCTION_DESCRIPTIONS.ID` — removido o texto hardcoded. Fonte única: uma revisão de redação toca um só arquivo.
- ⚠️ **Divergência sinalizada (`/identity`):** o enunciado citou `/identity`, mas essa tela (`identity-posture-dashboard`) é **PR.AA/GV.RR** (postura do Entra ID), NÃO a Função Identify — mantém o blurb próprio ("estilo Purple Knight"). A descrição de **ID** (inventário + Raio de Explosão) pertence a **`/assets`** (a landing real de ID.AM), onde foi centralizada. Forçar o texto de ID em `/identity` seria erro semântico.

---

## 10. Enriquecimento do Contrato de Controle para a IA (severidade · telemetria crua · plano · confiança · ameaças · MTTD/MTTR)

**Objetivo:** dar ao card de controle NIST o **esqueleto de dados estruturado** que a camada de IA vai preencher, e a leitura visual de risco que o analista precisa ter num relance.

⚠️ **PREMISSA VENCIDA (era "terreno, não motor") — NÃO redescobrir.** Este bloco dizia que nenhum produtor preenchia o enriquecimento e que "o System Prompt **não o pede**, de propósito". **Isso mudou:** o `BuildSystemPrompt` do `AegisAiEvaluatorService` **PEDE** o bloco `intelligence` (contrato de saída com `severity`/`aiConfidenceScore`/`threatLandscape`/`remediationPlan`) e o **Gemini real o preenche por completo** — validado ao vivo em 2026-07-17/18 (ver **seção 11**). O que segue vazio é só o caminho de DEV do `StubLlmClient` (determinístico, não emite o bloco) e o `HistoricalCompliance` (sem produtor possível — ver 10.3 item 3). **Zero mock permanece a decisão:** campo vazio é honesto, campo preenchido por suposição é auditoria falsificada. Build .NET verde (0/0) + **115/115 testes** + `ng build` AOT verde.

### 10.1 Backend — o blob de inteligência (mesmo idioma do `ChecksJson`)

- **Régua de severidade (`Domain/Common.cs`):** enum `SeverityLevel {Critical=0, High, Medium, Low, Informational}` — o valor numérico É o rank de risco (ordenar já traz o crítico ao topo) — + o helper puro `SeverityLevels.FromStatus` (idioma de `AuditorScopes`): `NonCompliant→Critical`, `MitigatedByThirdParty→Medium`, `Compliant→Low`.
- **Contratos (`Application/Telemetry/Models/`):**
  - `ControlIntelligence.cs` (NOVO) — `record ControlIntelligence` com TODOS os membros opcionais (`Severity`, `TelemetryEvidence`, `RemediationPlan`, `AiConfidenceScore` (double), `ThreatLandscape`, `MttdMinutes`, `MttrMinutes`): o motor preenche o que consegue PROVAR e o card renderiza seção a seção. No mesmo arquivo, `record TelemetryEvidence(SourceTool, RawTrace, CollectedAt?)` — o rastro CRU da ferramenta (EntraID/SentinelOne), distinto de `AiEvidence` (a prosa JÁ interpretada do motor).
  - `ComplianceHistoryPoint.cs` (NOVO) — `record (DateOnly Date, int CompliancePercent)`, o ponto da sparkline de 30 dias. Granularidade tenant × subcategoria, ≠ `TenantTrendDto` (agregado do tenant).
  - ⚠️ Nomenclatura: o enunciado diz `MTTD`/`MTTR`; viraram **`MttdMinutes`/`MttrMinutes`** (unidade no nome, idioma do `MeanTimeToAcknowledgeMins` que a telemetria já usa).
- **Seam motor→ledger:** `ComplianceVerdict.Intelligence { get; init; }` (espelha `Checks`); `IControlStateWriter.ApplyVerdictAsync` ganhou `ControlIntelligence? intelligence = null` **entre `checks` e `ct`**. ⚠️ **O `AegisAiEvaluatorService` passava o `ct` POSICIONALMENTE** (`BlastRadiusScoreProjector` e `DocumentAnalysisWorker` já nomeavam) — corrigido para `ct:`. O fake `RecordingControlStateWriter` (`BlastRadiusScoreProjectorTests`) acompanhou a porta.
- **Persistência:** `TenantControlState.IntelligenceJson` (text, nullable) — **mesma decisão do `ChecksJson`**: blob explicável de LEITURA, sem modelagem relacional (não há consulta por campo; normalizar custaria joins sem pagar nada). Migration **`20260716212615_AddIntelligenceJsonToControlState`** (só `AddColumn`; `has-pending-model-changes` limpo). Aplica no boot (`MigrateAsync`) — não rodar `database update` à mão.
- **`ControlStateWriter`:** serializa o blob na escrita. ⚠️ **Quem não emite ZERA o campo** — não herda a inteligência do veredito anterior: inteligência órfã descreveria um estado que não existe mais. `DeserializeChecks` virou casca fina sobre um `SafeDeserialize<T>` genérico (tolera JSON corrompido → null; um blob quebrado não pode derrubar a leitura do ledger, o score é o crítico).
- **`AegisAiEvaluatorService`:** `VerdictJson` ganhou `intelligence`; as opções JSON ganharam **`JsonStringEnumConverter`** — ⚠️ **sem ele**, um `"severity":"Critical"` do LLM lançaria `JsonException` → `AiUnavailableException`/503. **O System Prompt NÃO foi tocado**: só o lado de RECEBER ficou pronto.
- **Leitura (`ControlStateDashboardQuery`):** o `Row` agora carrega os **enums CRUS** (`ControlStatus`/`VerdictSource`, não mais `.ToString()` na projeção) — o status decide a severidade-proxy antes de achatar. O novo `ToDto` desserializa o blob e o **ACHATA** no DTO: o frontend recebe objeto plano e não sabe que existe blob. `Severity = intel?.Severity ?? SeverityLevels.FromStatus(status)` — o card nunca fica sem badge.
- **DTO (`TenantControlStateDto`):** os 8 campos entraram como **props `init` aditivas**, não parâmetros posicionais (o record já tinha 9 posições; é o idioma do `ComplianceVerdict.Checks`). Enum vira string na fronteira (`Severity`).

### 10.2 Frontend — régua única de severidade + o dossiê do controle

- **⚠️ `SeverityLevel` MUDOU DE CASA:** de `identity.models.ts` → **`scoring.models.ts`**, junto com `severityForStatus` (severidade não é assunto de identidade: TODO controle NIST tem uma). `identity.models` os **REEXPORTA** (`export type { SeverityLevel }; export { severityForStatus }`) para não quebrar importadores; `severity.component.ts` passou a importar de `scoring.models`.
- **Novos tipos (espelham o backend, camelCase):** `TelemetryEvidence {sourceTool, rawTrace, collectedAt}` e `ComplianceHistoryPoint {date, compliancePercent}`. `TenantControlStateDto` e `ControlView` ganharam severidade, histórico, evidência de telemetria, plano, confiança, ameaças e MTTD/MTTR. `toControlView` usa `??` em TODOS os novos campos — o backend pode ser mais velho que o front; o card degrada seção a seção, nunca quebra.
- **`PillarView.mttdMinutes/mttrMinutes`** via `averageOf` (função pura nova): média só dos valores REPORTADOS, `null` se ninguém reportou. ⚠️ **nulo ≠ 0** — tratar ausência como zero faria o HUD anunciar detecção instantânea. `formatDuration(min)` (pura, em `scoring.models`): "18 min" · "2h 30m" · "1d 4h" · "—".
- **`PillarMeta.showsResponseMetrics`** (campo NOVO em `PILLARS`): `true` só em DE/RS/RC — não existe "tempo de detecção" de política de governança. É CONFIG, não `if` espalhado: mantém os 4 painéis como UM componente.
- **`components/scoring/sparkline.component.ts` (NOVO — dumb, SVG puro, zero libs):** série de 30d do controle; cor pela MESMA faixa do `ScoreGauge` (≥80 cyan · ≥50 âmbar · <50 vermelho) aplicada ao valor ATUAL; último ponto marcado (é o hoje). ⚠️ **Se omite com < 2 pontos** — uma reta insinuaria estabilidade sem dado que a sustente. ⚠️ `series` é `protected`, não `private`: template Angular não enxerga membro privado (quebrou o build AOT na primeira tentativa).
- **`control-compliance-card`:** head virou grid de **7 colunas** (dot · nomes · **sparkline** · **severidade** · status · pontos · chevron); o slot `.spark` existe SEMPRE (vazio sem série) para a linha não pular. Sob 860px a sparkline some e a severidade FICA (é o sinal de risco). O corpo expandido virou um DOSSIÊ: checklist (existente) → evidência da IA (existente) → **AI Confidence Score** (trilha `flex:1` + número fixo à direita — a MESMA correção do blast-radius, ver 4.4) → **Evidência da Telemetria** (chip da ferramenta + `<pre>` com `max-height:168px` e rolagem própria: um dump de log não pode empurrar o plano para fora) → **Mapeamento de Ameaças** (chips vermelhos) → **Plano de Ação (Remediação)** → `.meta` (rodapé). Cada seção só renderiza se o dado existe.
- **⚠️ O advisory NÃO foi duplicado:** o fluxo "✦ Gerar Recomendação" (Fase 2, seção 8) foi RE-ANINHADO **dentro** da seção "Plano de Ação", abaixo do `remediationPlan` inline. Semântica: `remediationPlan` = o "o quê" de relance (LLM); `RemediationAdvisory` = o "como fazer" persistido e exportável, sob demanda.
- **`pillar-dashboard`:** cards HUD **MTTD/MTTR** no topo (acima do grid), só quando `meta().showsResponseMetrics`. Sem medição → classe `.void` (apagado) + "—", **nunca "0 min"**.
- Budget de CSS do card: 5.47 kB → **7.07 kB** (aviso não-fatal, consistente com a postura do projeto; o bundle gera).

### 10.3 Divergências do enunciado (decisões Principal — não redescobrir)

1. **Severidade tem 5 níveis, não os 4 do enunciado.** O front JÁ tinha a escala de 5 (Purple Knight, em `identity.models`) e o `SeverityComponent` JÁ desenha 5 pips. Um enum de 4 no backend criaria **duas réguas de risco divergentes** no mesmo produto. Régua única, 5 níveis, com `Informational`.
2. **`RemediationPlan` não substitui o motor de Advisories** (seções 7 e 8) — ver 10.2. Dois campos, dois papéis.
3. **`HistoricalCompliance` é o ÚNICO campo sem produtor POSSÍVEL hoje.** Não existe snapshot POR CONTROLE: o `AegisScoreSnapshotWorker` só tira a foto AGREGADA do tenant (`TenantScoreSnapshot` → `/trend`). A query entrega `Array.Empty<>()` e a sparkline se omite. Preencher = tabela nova + worker + migration (fatia real, não prompt engineering).

**Verificação:** build .NET 0/0, **115/115 testes** (nenhum teste novo — a fatia é contrato + UI, sem regra de negócio nova), `ng build` AOT verde, migration sem drift. ✅ **AGORA VALIDADO AO VIVO (2026-07-17)** — via o andaime de mock da **seção 10.4** (o dossiê de IA renderizou ponta a ponta; ver lá). O observável sem o mock, subindo API + `ng serve`: badge de severidade em TODO controle (via proxy do status) e os cards MTTD/MTTR em "—" nas abas DE/RS/RC.

### 10.4 Andaime de mock DEV — ✅ **APOSENTADO E REMOVIDO em 2026-07-18** (registro histórico)

⚠️ **NÃO recriar.** Este andaime existiu por ~1 dia para provar que o dossiê de IA renderizava enquanto não havia produtor. **A muleta caiu**: a seção 11 ligou o produtor real e o ledger passou a ter inteligência gravada pelo Gemini, então a flag `useMockScoring`, o `data/scoring-mock.data.ts` e o curto-circuito no `ScoringService.getDashboard()` foram **DELETADOS** (`ng build` verde sem eles; zero referências remanescentes). O `/scoring/dashboard` volta a ser a fonte ÚNICA do HUD. O que segue abaixo é o registro do que foi feito, não instrução vigente.

**Contexto (histórico):** a seção 10 trafegava VAZIO (sem produtor), então o dossiê de IA nunca tinha sido visto renderizado. O mock era DEV-only atrás de flag — **não feria o princípio "zero mock" do ledger honesto**: o backend seguia vazio; era andaime de UI client-side, explicitamente reversível, não falsificação do estado do tenant.

- **`data/scoring-mock.data.ts` (NOVO):** `SCORING_MOCK_DASHBOARD: TenantControlStateDto[]` — espelha o contrato de `/scoring/dashboard`, com o bloco `ControlIntelligence` preenchido em TODOS os campos. Cobre o eixo de severidade de ponta a ponta (PR.AA-01 Crítico/NonCompliant enriquecido ao máximo · PR.DS-01 Médio/Parcial · PR.IR-01 Baixo/Compliant) + DE.CM-01 e RS.MA-01 (para o HUD MTTD/MTTR sair do "—"). Códigos NIST escolhidos para resolverem no glossário (`categoryName`).
- **`environment.ts`:** flag `useMockScoring: true`.
- **`services/scoring.service.ts`:** `getDashboard()` curto-circuita para `of(SCORING_MOCK_DASHBOARD)` quando a flag está ligada. ⚠️ **Desvio na FONTE de dados, não no componente dumb** — o `ControlComplianceCardComponent` recebe `input.required<ControlView[]>()`; forçar o mock nele quebraria o fluxo Smart/Dumb. Assim os 4 painéis acendem pelo pipeline real (`buildPillarView` → card), sem tocar na renderização (que já estava correta desde 10.2).
- ✅ **VALIDADO AO VIVO (2026-07-17, `ng serve` porta 5273):** `/protect` renderizou gauge 45%, 3 controles ordenados (NonCompliant no topo). Expandido o PR.AA-01 Crítico, o dossiê saiu completo e no tema HUD — verificado via DOM (`getComputedStyle`, pois screenshot trava com HMR, ver [[aegis-frontend-verification]]): **AI Confidence 94%** com barra cyan `rgb(38,224,255)` (faixa ≥80 correta), Evidência da Telemetria (chip EntraID + `<pre>` do JSON cru), 4 chips de Mapeamento de Ameaças, Plano de Ação + botão "✦ Gerar Recomendação", checklist ✓/✕ e badge CRÍTICO. **Todas as seções da 10.2 provadas renderizando.**
- ⚠️ **Rotas de pilar são guardadas (`authGuard`):** para a prévia sem subir a API .NET, semeei o token em memória pelo console do Angular (`ng.getComponent(app-login).auth._accessToken.set(...)` → `router.navigateByUrl('/protect')`) — token efêmero, some no reload, **sem alterar código** (técnica registrada em [[aegis-frontend-verification]]).
- ✅ **REVERTIDO em 2026-07-18** (flag + arquivo + `import`/`if` removidos; `ng build` verde; zero referências).

**Escopo/backlog (em ordem de valor):**
1. ~~**Ligar um produtor**~~ ✅ **CONCLUÍDO e VALIDADO CONTRA A API REAL** (ver **seção 11**): o System Prompt pede o bloco `intelligence` e o Gemini o preenche por completo (severidade, confiança, ameaças MITRE, plano). *(Pendência fina: o `StubLlmClient` do caminho DEV segue sem emitir o bloco — só o motor real produz inteligência.)*
2. **MTTR é quase de graça; MTTD NÃO TEM FONTE.** ⚠️ Verificado no código (2026-07-16): `EvaluateRespondRecover` já lê `Num(p, "mean time to respond:")` para reprovar RS.MI-01 — o valor decide o veredito e é **descartado**; projetá-lo em `MttrMinutes` é trivial. **Já o MTTD não existe em lugar nenhum** — `EvaluateDetect` não lê tempo algum (DE.AE-02/DE.CM-01/DE.AE-06 usam contagem de anomalias, cobertura de log e cobertura MITRE). ⚠️ **NÃO aliasar `MeanTimeToAcknowledgeMins` (RS.MA-01) como MTTD**: MTTA é o tempo de *reconhecer o alerta*, MTTD é o de *detectar a ameaça* — são métricas de SOC distintas, e trocá-las reportaria número errado num produto de conformidade. MTTD real exige ingestão nova (campo no `AnomaliesTelemetryDto`/`MonitoringTelemetryDto` + rótulo lido pelo Stub).
3. **Severidade real ponderada pelo Raio de Explosão:** o `BlastRadiusScoreProjector` (2.9) já escreve no ledger via `IControlStateWriter` e agora tem o parâmetro `intelligence` — é o candidato natural a preencher `Severity` + `ThreatLandscape` (as `AssetThreatExposure`/`Threat` do tenant já mapeiam os vetores, ex.: T1486).
4. **Snapshot por controle** → `HistoricalCompliance` + sparkline viva.

---

## 11. Teste de Fogo — Motor RAG e Inferência Algorítmica VALIDADOS contra a API real

> **Motor RAG e Inferência Algorítmica validados.** O motor **lê o `jsonb`**, **respeita o fail-closed matemático**, **preenche o `ControlIntelligence` completo** e **possui guard-rail contra alucinação de falso-positivo.**

Executado em **2026-07-18** contra o **Gemini REAL** (`gemini-flash-latest`), sem mock em nenhuma camada. Fecha o backlog #1 da seção 10 e aposenta o andaime de mock (10.4).

### 11.1 O caminho provado ponta a ponta

`AegisAssessmentRule` (jsonb, 97 regras) → **RAG por chave** (`AssessmentRuleContextBuilder`) → System/User Prompt → **Gemini real** → parse do bloco `intelligence` (com `JsonStringEnumConverter`) → `ControlStateWriter` → **ledger**. Nenhuma etapa simulada.

### 11.2 Harness (⚠️ DEBUG-ONLY)

- **`Api/Dev/RagFireTestScenarios.cs`** — telemetria FORJADA. ⚠️ **NÃO existe tipo `TelemetryEvent` no domínio** (o seam do avaliador é `string rawTelemetryPayload`, de propósito: o motor precisa ver o log CRU, não um DTO já interpretado); o record é local ao andaime, só para gerar JSON realista.
- **`Api/Controllers/DevRagFireTestController.cs`** — `#if DEBUG`, `[AllowAnonymous]`, `GET` descreve / `POST` dispara (`?scenario=all|A..D&control=`). Monta o avaliador **à mão** porque as dependências scoped estão presas ao DbContext da requisição, cujo tenant HTTP é nulo num endpoint anônimo — usa um `ITenantContext` não-HTTP (idioma do `DevController.SystemTenantContext`). O `ILLMClient`, esse, vem do DI: é o ponto do teste.
- ⚠️ **ESCREVE NO LEDGER** do tenant demo (o avaliador persiste por design) — é o que encheu `/scoring/dashboard` com inteligência real e permitiu matar o mock do front.
- ⚠️ **NÃO é teste automatizado e não deve virar um.** Imprime a EXPECTATIVA ao lado do veredito para conferência humana: um LLM não é determinístico e um `Assert` aqui seria um teste instável que mentiria nas duas direções.

### 11.3 A matriz de prova (4 cenários, cada um isolando UMA afirmação)

| # | Payload forjado | Controle | Prova | Resultado real |
|---|---|---|---|---|
| **A** | USB em host restrito (WINEVTLOG 133/134) | PR.DS-01 | **Guard-rail anti-alucinação** | ✅ `NonCompliant` — *"o payload não contém dados para o cálculo da fração de endpoints cifrados… (SC-13) ou gestão de chaves (SC-28)"* |
| **B** | Alerta EDR crítico (Sentinel) | DE.CM-01 | **Troca de regra por chave** | ✅ `NonCompliant` — citou *"baseline de 214 dias (limite de 180)"* e *"64,2% (27 de 42 interfaces)"* |
| **C** | Cifragem ACIMA da rubrica | PR.DS-01 | **Cálculo da fórmula** | ✅ `Compliant 20/20` — *"score de **0.96** (acima do limiar de 0.85)"* |
| **D** | Cifragem ABAIXO da rubrica | PR.DS-01 | **Limiares + fail-closed** | ✅ `NonCompliant 0/20` — *"RC4/3DES **zera o índice criptográfico (SC-13)**… score final de **0,48** (abaixo do mínimo de 0,50)"* |

**A prova decisiva (C e D):** os números do payload foram escolhidos para que a `calculation_logic` do PR.DS-01 (`.30·endpoint + .30·repositório + .25·chaves + .15·algoritmo`) desse um resultado ÚNICO conferível à mão — **0,9647** e **0,4795**. O Gemini reportou **0.96** e **0,48**. Ele não classificou por "vibe": **executou a rubrica do jsonb**. Em D acertou também a cláusula fail-closed (algoritmo fora do padrão validado ⇒ fator 0, independente da cobertura).

**`ControlIntelligence` veio COMPLETO** nos 4: `severity` (`High`/`Informational`), `aiConfidenceScore` (100), `threatLandscape` com MITRE real (`T1052.001 · Exfiltration Over Physical Medium`) e `remediationPlan` acionável em PT-BR. Latência 5,5–8,9 s por controle.

### 11.4 Modelo Gemini — default trocado (⚠️ não reverter para um pin)

Sondagem direta da Generative Language API (2026-07-18): `gemini-2.0-flash` e `gemini-2.0-flash-lite` → **429 RESOURCE_EXHAUSTED** (cota free esgotada, **persistente** desde 2026-07-13 — não é transitório); `gemini-2.5-flash` → **404** ("no longer available to new users"); **`gemini-flash-latest` → 200 OK**.

**`AegisAiOptions.Model` e `appsettings.json` agora usam `gemini-flash-latest`** — alias, não pin, de propósito: um modelo pinado envelhece para 404/429 e derruba a avaliação com 503. Sobrescrevível por `AegisAi:Model`/`AegisAi__Model` quando um pin for necessário. **115/115 testes verdes** após a troca (nenhum teste fixava o modelo).

### 11.5 Divergências do enunciado (decisões Principal — não redescobrir)

1. **`TelemetryEvent` não existe** — ver 11.2.
2. **A DI do Gemini e o System Prompt já estavam prontos** — os passos "cabear o `ILLMClient`" e "pedir o bloco de IA" do enunciado já estavam feitos; o trabalho real era o harness + os cenários.
3. **`tenantId` fictício é impossível** — o avaliador exige `tenantId == tenant do contexto` (fail-fast) e o writer precisa da FK real do tenant. Usa-se o `DemoTenantId` (rodar `POST /dev/seed-demo` antes).
4. **Os 2 cenários do enunciado não exercitavam a rubrica do PR.DS-01** (ela mede cifragem/chaves/algoritmo; USB e execução suspeita não são medíveis por ela). Em vez de descartá-los, viraram os testes de **guard-rail** (A) e **troca de regra** (B) — e C/D foram acrescentados para provar a matemática. É o que transformou a demo em prova.

---

## 12. Polimento de UI/UX — casca, rolagem, FAB da égide e alinhamento da tabela de ativos

Duas rodadas de acabamento (2026-07-18), todas validadas no DOM real. **Arquivos: `app.component.ts`, `pages/asset-inventory.component.ts`, `pages/document-hub.component.ts`.**

### 12.1 ⚠️ Premissa recorrente e FALSA: "o SCSS do sidebar"

**NÃO existe SCSS neste projeto. NÃO existe `sidebar.component.*`. NÃO existe `app.component.html`.** Dois enunciados seguidos pediram para editar `sidebar.component.scss` — o arquivo nunca existiu. A casca inteira (marca, navegação, drawer, FAB) vive **inline** no `app.component.ts` (`template:` + `styles: [\`…\`]`); o ÚNICO `.html` do repositório é `pages/aegis-dashboard.component.html`. Antes de "editar o SCSS de X", conferir que X existe.

### 12.2 Sidebar — rolagem no idioma Synapse

`overflow-x: hidden` + `::-webkit-scrollbar` de 6px (track transparente, thumb `rgba(255,255,255,.1)` → `.22` no hover) + `scrollbar-width`/`scrollbar-color` (as regras `::-webkit-` são ignoradas pelo Firefox).

⚠️ **A causa REAL da barra horizontal era o `.sidebar::after`** — a aresta neon estava ancorada em `right: -1px`, 1px FORA do box, e era ela sozinha que criava o transbordo. Reancorada em `right: 0`: transbordo foi de 1px para **0** (barra morta na origem, não escondida) e a aresta deixou de ser recortada pelo novo `overflow-x: hidden` — que a teria apagado.

### 12.3 Seção "Referência" + link externo

Novo `nav-group` no fim da navegação com "Sobre o NIST CSF 2.0" → `https://www.nist.gov/cyberframework`. Sem `routerLink`/`routerLinkActive` (é `href` externo, não rota) e **com `rel="noopener noreferrer"`** — `target="_blank"` sem isso é vetor de tab-nabbing. O rótulo trunca (`text-overflow: ellipsis`), obrigatório sob o `overflow-x: hidden`.

### 12.4 FAB global do Auditor (saiu do sidebar) + escudo da égide

O gatilho do Agente virou **FAB fixo** no layout raiz: `position: fixed; bottom: 30px; right: 30px; z-index: 9999`, 58×58 (50×50 sob 960px), `border-radius: 50%`, halo neon + sombra de elevação.

⚠️ **O `z-index: 9999` colidia com o drawer** (`z-index: 60`, painel ancorado em `right: 0` — abre exatamente sobre o FAB). Solução: com `agent.open()` o FAB recebe `.is-hidden` (`opacity: 0`, `scale(.8)`, `pointer-events: none`) — ali ele é redundante, o drawer tem o próprio ✕. **NÃO baixar o z-index** para "resolver": ele precisa flutuar sobre todo o conteúdo.

**Ícone:** SVG inline `viewBox="0 0 32 32"` a 26px — anel com o gradiente da marca (`#26e0ff → #8b5cff → #ff3d9a`), 8 traços radiais (serpentes), rosto em triângulo invertido, olhar ciano. Geométrico de propósito: em 26px detalhe figurativo vira sujeira. Substituiu o glifo `✦`.

**O botão duplicado do Govern FOI REMOVIDO** (`document-hub`, `.auditor-btn` + `@keyframes auditor-pulse` + seletor no bloco de reduced-motion). ⚠️ A injeção de `AgentStateService` no `document-hub` **PERMANECE** — é usada por `agent.coverageVersion()` (barramento reverso da entrevista GRC, ver 4.3). Validado: o FAB abre o drawer já escopado em "GOVERNAR (GV)".

### 12.5 ⚠️ Tabela de ativos — colisão com o utilitário GLOBAL `.grid` (a armadilha)

**O bug:** a tabela do inventário era `<table class="grid">` ("grade de dados"). Mas `styles.css:161` define `.grid { display: grid; gap: 18px }` — utilitário de LAYOUT, usado por `<div class="grid">` em executive/identity/pillar-dashboard. A regra global sequestrava a tabela: `display: grid`, `thead`/`tbody` viravam **grid items** (`display: block`), cada `<tr>` formava a própria tabela anônima e as colunas do cabeçalho computavam larguras independentes das do corpo. Medido: col 1 com `th` de 65,8px sobre `td` de 163,7px.

**A correção:** renomear a classe para **`.asset-table`** — remover a colisão, não compensá-la. Volta `display: table` e as colunas alinham **por construção do algoritmo de layout**. ⚠️ **NÃO "resolver" sincronizando `grid-template-columns` entre cabeçalho e linha** (o caminho sugerido no enunciado): seria abraçar o acidente, exigiria manter frações em sincronia para sempre e quebraria o `colspan="8"` da linha de estado vazio.

**Bug secundário (pré-existente):** nas colunas `.num` (Crit./Status) o cabeçalho ficava à esquerda sobre dado centralizado — `table.asset-table thead th` (que fixa `text-align: left`) vencia `table.asset-table th.num`. ⚠️ **Especificidade sob encapsulação do Angular:** os atributos `[_ngcontent-*]` injetados EMPATAM a contagem de classes, e o desempate vai para o seletor com mais ELEMENTOS. Corrigido tornando a regra `.num` explícita em `thead`/`tbody`. Vale para qualquer override em componente com styles encapsulados.

**Medição final:** 8/8 colunas com `deltaLeft = 0`, `deltaWidth = 0` e `text-align` idêntico, em 1280px e 820px. `.table-wrap` rola horizontalmente (intencional); a página não.

### 12.6 ⚠️ Duas armadilhas de FERRAMENTA (custaram iterações — não repetir)

1. **Crase em comentário CSS quebra o build.** Os estilos são template literals JS; uma crase dentro de `/* … */` ENCERRA a string → `FatalDiagnosticError Code 1010: Failed to resolve styles at position 0`. Usar aspas em comentários. **Pior: o `ng serve` NÃO se recupera de falha na INICIALIZAÇÃO do compilador AOT** — segue servindo bundle velho e devolve leituras enganosas até ser reiniciado. Sintoma: o fonte tem o fix, a folha servida tem a regra antiga.
2. **A aba do Browser pane roda com `visibilityState: "hidden"`** — o compositor CONGELA transições de `opacity`/`transform` em `currentTime: 0`. Ler essas propriedades logo após um toggle dá o valor INICIAL, não o final (foi o que fez o FAB "parecer" visível com o drawer aberto). Antes de medir: `el.getAnimations().forEach(a => a.finish())`. Propriedades não animadas (ex.: `pointer-events`) aplicam normalmente — a divergência entre elas é a assinatura do problema. Complementa [[aegis-frontend-verification]] (screenshot trava com HMR).
3. ⚠️ **`transition` em CSS tem o MESMO efeito de frame congelado** (a variante que custou mais caro, 2026-07-18). Uma aba com `.tab.on { color: var(--red) }` lia `--muted` no `getComputedStyle` — não por especificidade, mas porque a `transition: color .15s` não avançava com a página em background. Diagnóstico rápido: setar `el.style.transition = 'none'` e reler; se o valor muda, era medição, não CSS. Perseguir isso como "bug de especificidade" gerou várias iterações inúteis.
4. ⚠️ **Com `ng serve` (HMR), o CSS do componente pode ficar CORROMPIDO em runtime** — ao forçar recomputação de estilo, TODAS as regras do componente sumiram (tudo virou `rgb(0,0,0)`), e `document.styleSheets` deixou de enxergá-las (os estilos vão para `adoptedStyleSheets`). **Para medir estilo com confiança, servir o BUILD:** `ng build` + `python -m http.server` na porta liberada pelo CORS. ⚠️ O `http.server` não tem fallback de SPA — deep-link dá 404; entrar pela raiz e navegar pelo menu.
5. ⚠️ **O budget de CSS do Angular mede o CSS MINIFICADO.** Compactar formatação e comentários não muda o número em nada — só remover REGRAS. Quando o `control-compliance-card` estourou os 8 kB, a saída certa foi **extrair um componente** (`missing-requirements`), não afrouxar o `angular.json`: cada componente tem seu próprio budget, e 8 kB de CSS num só arquivo já era o sintoma.

---

## 13. Persona do Auditor Virtual (System Prompt em 3 blocos)

**Objetivo:** tirar o Auditor do "eco de siglas" e colocá-lo como consultor sênior, SEM que o tom possa contaminar o veredito. Testes 115 → 120.

- **`Api/Data/AuditorPersonality.json`** (copiado para o output via `csproj`): objeto `AuditorConfig` com `Persona`, `Tone[]`, `TranslationRules[{Code, BusinessTerm}]` (16 famílias NIST → impacto operacional) e `ActionDirectives[]`.
- **`Application/Services/IAuditorPersonaProvider.cs`:** records `AuditorPersona` / `AuditorTranslationRule`, o método puro `ToPromptBlock()`, `AuditorPersona.Neutral` e `StaticAuditorPersonaProvider` (usado como fallback e nos testes).
- **`Infrastructure/Ai/AuditorPersonaProvider.cs`:** singleton, lê o JSON UMA vez na construção. ⚠️ **FAIL-SOFT de propósito**, ao contrário do `FrameworkSeeder` (que aborta o boot): sem catálogo de regras a plataforma MENTE sobre a postura; sem persona ela só escreve mais seco. Arquivo ausente/malformado → `Neutral` + `LogWarning`.
- **`AegisAiEvaluatorService.BuildSystemPrompt(persona)`** virou 3 blocos, nesta ordem deliberada: **(1) `AssessmentRubric`** (rigor, fail-closed, anti prompt-injection) → **(2) persona** (omitida se vazia) → **(3) `OutputContract`** (JSON estrito + **Regra de Ouro da Tradução**: a 1ª frase do `remediationPlan` é o impacto no negócio, só então os passos técnicos, fechando com UMA oferta proativa).
- ⚠️ **Duas fronteiras que não podem cair:** (a) o bloco de persona AFIRMA ao modelo que ele governa tom e redação, **jamais** status/confiança/evidência; (b) `aiEvidence` permanece **forense** — a didática vale só para o `remediationPlan`. A Regra de Ouro vive no CÓDIGO (`const OutputContract`), não no JSON: o JSON ajusta tom sem recompilar, mas o formato que o Angular desserializa é contrato de software.
- **Testes (`AuditorPersonaTests`, 5):** o de maior valor carrega o **`AuditorPersonality.json` REAL** (linkado no csproj de teste) — como o arquivo é editável sem recompilar, um erro de digitação degradaria o Auditor em silêncio.

---

## 14. `MissingRequirements` — telemetria ausente × documentação ausente

**Objetivo:** responder "por que este controle não pontua?" com ESTRUTURA, não prosa. Telemetria ausente e política ausente têm donos, prazos e orçamentos diferentes. Testes 120 → 137.

### 14.1 Domínio e persistência

- **`Domain/ControlState.cs`:** enum `ComplianceRequirementType {Telemetry, Documentation, Both}` + `record MissingRequirement(Type, SourceIdentifier, Description)` + `TenantControlState.MissingRequirements` (`List<>`, default vazia).
- ⚠️ **Eixo ORTOGONAL ao `VerdictSource`** (fácil de confundir): `VerdictSource` diz o que PRODUZIU o veredito; este diz o que FALTA para prová-lo. Um controle avaliado por telemetria pode estar devendo documentação.
- **EF:** `jsonb` com `ValueConverter` + `ValueComparer` — o idioma das listas do catálogo NIST, **não** o string-blob de `ChecksJson`/`IntelligenceJson` (esta lista é percorrida e agregada por `Type`, não repassada opaca à UI).
- ⚠️ **Enum como TEXTO no jsonb** (`JsonbEnumAwareConverter`): o ledger é auditado direto no SQL, e `{"Type":1}` além de ilegível **mudaria de significado** se alguém reordenasse o enum. Tem teste lendo a coluna crua.
- **Migration `*_ControlState_MissingRequirements`:** aditiva, `jsonb NOT NULL DEFAULT '[]'`. ⚠️ Usa `defaultValue: "[]"` e **não** `defaultValueSql: "'[]'::jsonb"` — o cast `::jsonb` é sintaxe exclusiva do PostgreSQL e quebrou o `EnsureCreated` dos testes (SQLite): **61 testes falharam** na primeira tentativa.
- **Invariante no `ControlStateWriter`** (escritor único): status `Compliant` ⇒ lista VAZIA, ainda que o chamador envie itens. `MitigatedByThirdParty` **preserva** — o risco está coberto por terceiro, a dívida própria continua aberta.

### 14.2 `RuleEvaluator` — o motor ÚNICO da distinção

`Application/Assessment/RuleEvaluator.cs`, estático e puro (sem EF, sem LLM).

- ⚠️ **O schema real do `aegis_assessment_rules.json` NÃO tem `required_telemetry_source`/`required_evidence_type`** (campos citados em enunciados). São 4 campos: `subcategory_id`, `evaluation_metrics`, `calculation_logic`, `evidence_requirements`. A natureza da prova é INFERIDA do vocabulário de `evidence_requirements`, que é bimodal por construção: **`MANUAL_AUDIT_REQUIRED`** (39 regras → Documentation) × `"<Ferramenta>: <o que coletar>"` (58 → Telemetry). **Zero regras têm as duas.**
- **Uma lacuna por NATUREZA, não por ferramenta:** as fontes de telemetria de uma regra são ALTERNATIVAS (Sentinel *ou* SecOps *ou* CrowdStrike). Emitir uma por ferramenta diria ao operador que ele precisa dos cinco produtos.
- **Lacuna de PROVA ≠ lacuna de PRÁTICA:** se a telemetria chegou e mostrou MFA em 40%, o controle reprova mas **não** gera lacuna — o sinal existe. Reportar "falta telemetria" mandaria configurar um conector que já funciona.
- ⚠️ `Both` é **inalcançável pelo catálogo atual** (nenhuma regra mistura as naturezas). O código o suporta e tem teste; só aparece se o catálogo evoluir.

### 14.3 Produção e fronteira

- **`StubLlmClient`** emite `missingRequirements` quando reprova, **delegando ao `RuleEvaluator`** e lendo as fontes esperadas do próprio User Prompt (bloco `EXPECTED EVIDENCE SOURCES` que o `AssessmentRuleContextBuilder` já injeta) — sem tocar o banco. Marcadores de simulação: `telemetry source: absent`, `policy document: processed`.
- **System Prompt do motor real** ganhou o campo no contrato de saída, com a instrução de não reportar lacuna de prova para falha de prática.
- ⚠️ **`MissingRequirementDto` na fronteira** (`Application/Queries`): a API **não** tem `JsonStringEnumConverter` global, e `Type` é enum ANINHADO — sairia `"type": 1` e o Angular passaria a depender da ordem do enum C#. O DTO achata para string, como o resto do contrato já fazia com `.ToString()`.
- **Frontend:** `MissingRequirementsComponent` (dumb) + `groupMissingRequirements` (pura). Tom é SEMÂNTICO: telemetria = **vermelho pulsante** (o Aegis está CEGO, não sabe o estado), documentação = **âmbar** (dívida de processo; o controle pode até existir). Pintar as duas de vermelho apagaria a distinção que o recurso existe para criar. `MANUAL_AUDIT_REQUIRED` nunca vaza para a tela (vira "Auditoria Manual").
- ✅ **VALIDADO AO VIVO com o Gemini REAL:** PR.AA-01 → `Telemetry | Entra ID`; PR.AA-02 → `Documentation | MANUAL_AUDIT_REQUIRED`. Render confirmado por DOM (cores `rgb(255,45,111)` / `rgb(255,176,32)`, animação `gap-blink` ativa só no crítico).

---

## 15. Frescor do sinal (TTL) + validação de documento

**Objetivo:** um conector que morreu deixa linha no banco e o controle continuaria "coberto"; um upload sem processamento pareceria política vigente. Testes 137 → 146.

- **`Infrastructure/Scoring/ScoringOptions.cs`** ← seção `Scoring` do appsettings: **`DefaultSignalFreshnessHours: 72`** (cobre um fim de semana sem alarme falso). ⚠️ **Zero ou negativo DESLIGA** a checagem — uma config errada não pode transformar o painel inteiro em ponto cego.
- **`RuleEvaluator`** ganhou `EvidenceAvailability(LastTelemetryAt?, HasVerifiedDocumentaryCoverage)` + sobrecarga com janela e relógio injetado (`TimeProvider`). A descrição distingue **"nunca integrado"** de **"parou de reportar"** — o primeiro é configuração, o segundo é INCIDENTE (credencial revogada, agente caído).
- ⚠️ **O TTL vive na LEITURA (`ControlStateDashboardQuery.EnrichWithStaleness`), não na ingestão.** No instante em que o motor avalia, o payload é fresco por construção; cobrar TTL ali reprovaria o próprio dado sob análise. É ADITIVO — nunca apaga a lacuna que o motor persistiu.
- ⚠️ **A idade vem de `TenantControlState.LastEvaluatedAt` com fonte `Telemetry`, NÃO de `EvidenceSignal.CollectedAt`.** A esteira `/telemetry/*` **não grava `EvidenceSignal`** — só o `MicrosoftSecureScoreConnector` o faz. Cronometrar por ele marcaria como obsoleto TODO controle avaliado pela esteira principal (a maioria). Quando os conectores passarem a gravá-lo, o relógio vira `MAX(LastEvaluatedAt, EvidenceSignal.CollectedAt)`.
- ⚠️ **`IsVerified` não existe e não precisa existir.** `CoverageStatus` é `{NaoCoberto, Parcial, Coberto}` e o `SubcategoryCoverage` **já nasce só depois do RAG processar** (o `DocumentAnalysisWorker` grava `Coberto` acima do limiar de confiança, `Parcial` abaixo). Critério adotado, mais rigoroso: `Status == Coberto` **e** `EvidenceSource ∈ {Document, Both}` — `Parcial` significa que o RAG NÃO se convenceu, e cobertura via `Interview` é auto-declaração (deixaria o auditado atestar a si mesmo).
- ✅ **VALIDADO AO VIVO:** PR.IR-01 (114h) ganhou *"Sinal de Microsoft Sentinel OBSOLETO: último dado há 4 dia(s)"*; **GV.PO-01 (175h, o mais velho) ficou corretamente SILENCIOSO** — regra só-documental com cobertura aceita, sem eixo de telemetria a envelhecer.

---

## 16. RAG documental de 2 passadas + regras de Governança

**Objetivo:** julgar documento contra a régua do 800-53, com payload enxuto. Testes 146 → 160.

### 16.1 Por que DUAS passadas (a inversão do enunciado)

⚠️ **`GovernanceDocument` não tem campo de controle-alvo** — é o LLM que descobre quais controles o texto endereça. Logo é impossível "buscar a regra antes de chamar o modelo". O pipeline é:

1. **Triagem** (`AnalyzeDocumentAsync`) — texto truncado (`TriageCharBudget = 24k`), devolve os controles candidatos.
2. **Julgamento dirigido** (`EvaluateDocumentControlAsync`, NOVO) — por controle: carrega a `AegisAssessmentRule`, seleciona o trecho e envia **estritamente** trecho + controle + critérios (`ExcerptCharBudget = 6k`). Contratos `DocumentControlEvaluationRequest`/`DocumentControlVerdict`.

**Resiliência:** sem regra no catálogo, ou LLM indisponível → mantém a confiança da triagem. Refinamento é melhoria de precisão, não pré-requisito. O `Confidence` → `Coberto`/`Parcial` (limiar 0.7) **já existia**.

### 16.2 `DocumentChunker` — três correções que só o teste ao vivo revelou

`Application/Documents/DocumentChunker.cs`, puro e determinístico (sem embeddings: o ranking precisa ser auditável).

1. **IDF** — termos presentes em todo parágrafo não discriminam.
2. **Duas faixas de peso** — `primaryTerms` (peso 4) × `supportingTerms` (peso 1). ⚠️ A faixa primária são as **`evaluation_metrics`**, NÃO o outcome do catálogo: **o catálogo NIST é em INGLÊS e as políticas do cliente em PORTUGUÊS**; casamento léxico cross-língua não pontua nada, e o outcome deixava a escolha inteiramente nas mãos do vocabulário genérico de GRC.
3. **Piso de relevância (34% do topo)** — ⚠️ **orçamento é TETO, não cota a preencher.** Com 6k de teto e 400 caracteres pertinentes, TODO parágrafo com um termo solto entrava na carona. Efeito medido: trecho de PR.AA-01 de **5728 → 257 chars**.

⚠️ **Limite honesto:** coincidências léxicas isoladas ainda passam (a métrica de RC.RP-01 diz *"informação íntegra"* e o parágrafo de acessos diz *"Gerente de Segurança da Informação"*). Resolver de verdade exige embeddings semânticos — fora da stack atual.

### 16.3 Persona no caminho documental + regras GV.PO-01/GV.RR-01

- `ClaudeAssessmentService` passou a injetar `IAuditorPersonaProvider` (`WithPersona`) — era a lacuna real da §13, que só cobria telemetria.
- **`aegis_assessment_rules.json`: 97 → 99 regras.** GV.PO-01 e GV.RR-01 tinham ficado de fora e **caíam no fallback de triagem cega** justamente os dois controles centrais do Govern. `calculation_logic` é uma soma ponderada REAL (não o literal `"weighted_sum"`, que não é rubrica seguível) com **regra de corte fail-closed**: sem enforcement/aprovação, GV.PO-01 trava em 0.4; sem liderança nomeada, GV.RR-01 trava em 0.3.
- ⚠️ **`evidence_requirements` é um ÚNICO item contendo o token.** Quebrar em vários faria os itens sem `MANUAL_AUDIT_REQUIRED` serem classificados como **Telemetry** pelo `RuleEvaluator` — GV.PO-01 exibiria "Telemetria Ausente" com ícone de rede.
- ⚠️ **`FrameworkSeeder.SeedAssessmentRulesAsync` era `if (AnyAsync) return`** — com 97 regras no banco, o arquivo inteiro era ignorado para sempre e enriquecer o catálogo exigiria TRUNCATE manual. Agora é **INCREMENTAL**: insere só as ausentes, nunca sobrescreve o que está no banco.
- **`StubAssessmentService.AnalyzeDocumentAsync` deixou de ser canned** — devolvia sempre GV.PO-01/GV.RR-01, que não tinham regra, então **o RAG dirigido nunca era exercitado em DEV**. Agora roteia por tema e casa por RADICAL ("responsab", "registr", "revis"), não palavra exata — uma PSI real escreve "responsabilidade", "registradas".
- ✅ **VALIDADO AO VIVO (contraste):** PSI forte (contexto + sanções + CEO + RACI) → GV.PO-01 e GV.RR-01 **75% · COBERTO**; PSI fraca ("todos são responsáveis", "pretende adotar no futuro") → **55% · PARCIAL**. Mesma triagem (72%), destinos opostos.

---

## 17. Resiliência (Polly v8) + desligamento gracioso

Testes 160 → 165. Pacote `Microsoft.Extensions.Http.Resilience` 8.10.0.

- **`Infrastructure/Ai/AiResilienceExtensions.AddAiResilience()`**, aplicado no `DependencyInjection` aos dois HttpClients de IA: **retry exponencial com jitter** (3×, ~2/4/8s), **circuit breaker** (50% de falha, amostra 8, janela 30s, abre 20s) e **timeout de 60s por tentativa**.
- ⚠️ **O pipeline precisa ficar no HANDLER, não nos clients.** `GeminiLlmClient` e `ClaudeAssessmentService` traduzem qualquer não-2xx em falha de aplicação (`AiUnavailableException`/`EnsureSuccessStatusCode`) no instante em que a veem — um retry acima deles nunca enxergaria o 429.
- **`ShouldHandle` só o transitório** (429, 408, 5xx, falha de transporte). **401/403/404 falham na hora** — chave inválida e modelo aposentado (o caso real do `gemini-2.5-flash`) não melhoram com insistência.
- **Jitter não é enfeite:** sem ele, uma rajada que estoure a cota volta toda no mesmo instante e o 429 se perpetua em ondas sincronizadas.
- **`DocumentAnalysisWorker` — três defeitos de shutdown corrigidos:**
  1. `catch (Exception)` engolia o cancelamento e logava `Error` a cada parada do serviço (ruído de deploy no alarme).
  2. ⚠️ **`SaveChangesAsync(ct)` no `catch`** — se o cancelamento foi a causa, a própria gravação falhava e o documento ficava preso em **`Processing` para sempre**. Agora usa `CancellationToken.None` (limpeza de encerramento) e devolve a `Pending`.
  3. `RefineWithRuleAsync` capturava `TaskCanceledException` indiscriminadamente — ela chega por timeout do HttpClient (degrada) **e** por shutdown (deve propagar). O `when (!ct.IsCancellationRequested)` separa.
- **`RequeueOrphansAsync`** no arranque: a fila é um `Channel` **em memória**, então sem isso "devolver a Pending" só trocaria um limbo por outro. Best-effort — falhar aqui não impede o worker de subir.
- ⚠️ **Dois `NU1605`** resolvidos elevando pisos na linha 8.0.x: `Microsoft.Extensions.Http` 8.0.0 → **8.0.1** (exigido pelo Resilience) e, no projeto de teste, `Microsoft.Extensions.DependencyInjection` 8.0.0 → **8.0.1** (cadeia do EF Core).
- **Testes (`AiResilienceTests`, 5)** exercitam o **composition root real**, contando as tentativas que chegam à rede — a duração de ~17s é o backoff acontecendo de fato.

---

## 18. Auditoria do Dashboard Executivo (rastreabilidade · estado vazio)

- ⚠️ **Campos ÓRFÃOS ligados:** `exposure.overallMaturity` e `exposure.targetMaturity` eram enviados pelo backend e IGNORADOS. A tela recalculava a média local de `maturityByFunction` — que **diverge do servidor**: o `DashboardController` preenche com `0` toda Função sem avaliação (`agg?.CurrentScore ?? 0`) enquanto o rollup só promedia as que têm dados. Num tenant só com Govern em 3.0, o servidor diz **3.0** e a média local diria **0.5**. Defeito LATENTE (no tenant demo as 6 Funções têm dados, divergência 0.0) que se manifesta exatamente no cliente em onboarding. `generatedAt` também era órfão — agora no header.
- ⚠️ **Métrica × geometria são coisas diferentes:** `targetMaturity` (o alvo que a diretoria lê, do servidor) foi separado de `chartScale` (teto do gráfico = maior barra **e** maior alvo INDIVIDUAL). Usar o alvo agregado (4,18) como escala cortaria o alvo de RC (4,42) para fora da área útil.
- **Estado vazio:** o componente iniciava com `sampleDashboard` (dados fictícios) e um tenant zerado mostrava painéis em branco. ⚠️ **Zero num painel executivo lê como "nenhum risco", quando o correto é "nada foi medido"** — e essa diferença decide orçamento. Agora há estado de carga, `hasPosture()` com estado vazio + 3 passos de onboarding, e nota por painel (gaps, riscos por nível, matriz).
- **Sobre `NaN`:** `risk-levels` já se protege (`Math.max(1, …)`), `gap-chart` divide por constante; ⚠️ **`maturity-bars` divide por `max()`** e produziria `Infinity` com 0 — o `chartScale()` garante piso 4, mas o componente segue desprotegido para outros chamadores.
- **Trend NÃO existe no Executivo:** o `ExecutiveDashboardDto` não tem série temporal (o `ITenantScoreTrendQuery` alimenta `/scoring/trend`, consumido pelo `aegis-dashboard.component`). Não há dado estático a corrigir — há uma ausência.

### 18.1 Métricas de alto valor já calculadas e NÃO aproveitadas — ✅ **AS TRÊS FORAM IMPLEMENTADAS (ver §19)**

1. ~~**Tendência do Aegis Score no Executivo**~~ ✅ — `ITenantScoreTrendQuery` + `SparklineComponent`, ambos já existiam.
2. ~~**Lacunas por natureza consolidadas**~~ ✅ — `buildGapBalance` sobre o `missingRequirements` que o `/scoring/dashboard` já entregava.
3. ~~**Raio de Explosão (ID.RA)**~~ ✅ — endpoint novo `/dashboard/blast-radius-summary` + card na vitrine.

### 18.2 Código morto identificado (⚠️ NÃO removido — decisão do Felipe)

Varredura de todos os tipos e membros públicos de `src/` contra o corpus: **zero tipos órfãos**. Dois métodos sem nenhuma chamada, ambos **anteriores** ao arco 13–18:

- `MaturityScoringService.ToSnapshots` — mapeia para `MaturitySnapshot`, entidade **persistida**; cheira a fluxo projetado e não ligado.
- `RiskScoringService.HeatmapValue` — a fórmula `2×probabilidade + impacto`, plausivelmente a régua do heatmap.

### 18.3 O que NÃO foi verificado ao vivo (pendências honestas)

- **Estado vazio do Executivo** — não há tenant sem dados no `aegis_dev`; validado só por inspeção + build.
- **`RequeueOrphansAsync` com caso positivo** — o Stub processa sem rede e drena a fila antes de qualquer restart; a janela para criar um órfão é curta demais. Sem teste automatizado porque o worker vive em `AegisScore.Api` e a suíte referencia apenas `Infrastructure` (candidato: mover os workers para a Infra, onde já vivem os demais adaptadores).
- **Julgamento documental com o motor REAL** — `Ai:ApiKey` (Anthropic) segue não configurada; os percentuais observados vêm do `StubAssessmentService`. O encanamento está provado; a qualidade do julgamento depende do LLM lendo as rubricas.

---

## 19. Valor C-Level — as 3 métricas de alto impacto na vitrine executiva

Fecha o backlog da **§18.1**. Tudo validado ao vivo (build de produção servido, DOM real). `ng build` verde, **165/165** no backend.

### 19.1 Tendência — a DERIVADA do risco

Sparkline sob o gauge de Maturidade Geral + a variação ponta a ponta (**▲ 10 p.p.** em cyan na demo, série de 3 dias: 10,8% → 15,8% → 21,2%). Subir é cyan, cair é vermelho — a régua de cor do produto.

- ⚠️ **`fetchTrend()` JÁ EXISTIA** no `AegisScoreService` (consome `/scoring/trend`). Criar um segundo método no `DashboardService`, como o enunciado pedia, produziria **dois clientes para o mesmo endpoint**. Reusado.
- `trendToSparkline()` (`scoring.models`, pura) adapta `TenantTrendDto` → `ComplianceHistoryPoint`. O `SparklineComponent` foi escrito para a série POR CONTROLE, mas são os mesmos dois eixos com outros nomes — um segundo componente seria desperdício. Ele já se omite sozinho com < 2 pontos.

### 19.2 Balanço orçamentário — CAPEX × OPEX

Barra dupla + Top 3 pontos cegos, via `GapBalanceComponent` (dumb) alimentado por `buildGapBalance()` (pura). Na demo: **Ferramenta 71% (capex · 5 lacunas) × Processo 29% (opex · 2 lacunas)**.

- **Ordenação por PONTOS NIST em jogo** (`maxScorePoints - scorePoints`), não por contagem: fechar um controle de peso 20 vale mais que dois de peso 5.
- ⚠️ **`Both` conta nos DOIS lados** — é pendência que só fecha com as duas provas, logo onera os dois orçamentos. Por isso `telemetryCount + documentationCount` pode passar de `total`, e o denominador do percentual é a **soma dos lados**, não o número de controles.

### 19.3 Custo do fracasso — Raio de Explosão na vitrine

`BlastRadiusSummaryComponent`: `100 · Crítico` — *"Se **AD Domain Controller 01** for comprometido, o impacto se propaga por 6 ativos em até 2 saltos"*, com **2 processos de negócio atingidos** em destaque. ⚠️ Processos vêm ANTES de ativos de propósito: ativo é vocabulário de TI, processo é vocabulário de diretoria.

- **Endpoint NOVO `GET /api/v1/dashboard/blast-radius-summary`** + `BlastRadiusSummaryDto`.
- ⚠️ **DIVERGÊNCIA DELIBERADA do enunciado**, que autorizava expor isso no `ExecutiveDashboardDto`: o `/executive` já roda 6 consultas e É o que decide o First Contentful Paint. Pendurar mais um JOIN nele atrasaria a tela inteira por um painel secundário — contrariando a própria restrição de FCP do enunciado. Endpoint próprio ⇒ o painel carrega sozinho.
- **Barato por construção:** o `BlastRadiusAssessment` já MATERIALIZA `ImpactedAssetCount`/`ImpactedProcessCount`/`MaxDepth` no traversal — aqui é `ORDER BY BlastRadiusScore DESC … LIMIT 1` com um JOIN para o nome do ativo. Não há grafo a percorrer.
- **Escolhe o de MAIOR score, não o mais recente:** a diretoria pergunta "qual é o nosso pior cenário?", não "qual rodamos por último".
- ⚠️ **204 No Content** quando nunca houve cálculo (o `HttpClient` entrega `null`). "Nunca medimos" ≠ "o raio é zero" — o frontend precisa da distinção para escolher entre estado vazio e número. Daí `fetchBlastRadiusSummary(): Observable<BlastRadiusSummary | null>` e o signal `blastLoaded` separando "carregando" de "não existe".

### 19.4 FCP preservado — quatro cargas INDEPENDENTES

⚠️ **Nada de `forkJoin`/`combineLatest`.** Encadear as quatro chamadas faria a tela esperar a mais lenta. Cada painel tem o próprio signal e acende quando o seu dado chega; **só o `/executive` governa o `loading`**. Os três secundários falham com `console.warn` e se omitem — falha de um não derruba os outros.

### 19.5 ⚠️ Bug pego pela verificação ao vivo: vocabulário de máquina vazando

O primeiro render mostrou **`PR.AA-02 · MANUAL_AUDIT_REQUIRED`** na vitrine executiva. A tradução existia — mas como constante **LOCAL** do `MissingRequirementsComponent`, e o painel novo (escrito depois) não a herdou.

**Correção:** `MANUAL_AUDIT_TOKEN` e `sourceLabelOf()` subiram para `scoring.models.ts`; os dois componentes consomem de lá. **A segunda cópia de uma regra de apresentação é exatamente como token técnico chega à tela do board.** Corrigido junto: `CRITICO` → `Crítico` (vem do enum C# `RiskLevel`, sem acento por construção; a classe CSS segue usando o valor original em minúsculas).

### 19.6 CSS e pendências

- **Budget respeitado:** `GapBalanceComponent` e `BlastRadiusSummaryComponent` extraídos como dumb; **nenhum dos dois** aparece nos warnings, e o `executive-dashboard` também não. Os 4 avisos restantes são pré-existentes (`asset-inventory`, `app.component`, `document-hub`, `control-compliance-card`).
- ⚠️ **Trend pobre na demo (3 pontos)** — o `AegisScoreSnapshotWorker` tira a foto à meia-noite UTC. Reforça o backlog item 4 da §5 (snapshot no boot em DEV).
- **`buildGapBalance` e `trendToSparkline` não têm teste automatizado** — são funções puras e testáveis, mas o projeto não tem suíte de frontend (só `ng build`). Montar o harness é uma fatia própria.

---

## 20. Onboarding — `ITenantManagementService`, fechamento do IDOR e upsert protegido por índice

Consolidação do onboarding (tenants + conectores) num serviço de aplicação. Três regras que viviam soltas no `TenantsController` passaram a ter dono único: **normalização/unicidade do slug**, **cifragem estática das credenciais** e **vínculo ao tenant correto**. `dotnet build` da solução verde (0 erros / 0 avisos), **187/187** testes.

### 20.1 A porta na Application, o adapter na Infrastructure (⚠️ divergência do enunciado)

O enunciado pedia a implementação em `AegisScore.Application`. **Impossível:** o `.csproj` da Application referencia só o Domain — sem EF Core, sem `AegisScoreDbContext`. Um serviço que faz `_db.Tenants.Add(...)` não compila lá.

Seguido o padrão que o projeto já usa (`IControlStateWriter` → `ControlStateWriter`), inclusive já documentado no XML doc dele: *"A implementação vive na Infrastructure (toca o DbContext); a porta, aqui."*

- **Porta:** `Application/Services/ITenantManagementService.cs` — interface + commands (`CreateTenantCommand`, `ConfigureConnectorCommand`) + results (`TenantProvisioningResult`, `ConnectorConfigurationResult`).
- **Adapter:** `Infrastructure/Tenancy/TenantManagementService.cs`. Registrado como **Scoped** na `AddAegisScoreInfrastructure`.

**Erro esperado é VALOR, não exceção.** Slug duplicado/malformado viajam no `TenantProvisioningStatus` (→ 409/400 na borda), porque o `GlobalExceptionHandlingMiddleware` traduziria qualquer throw num 500 opaco. Só `TenantSecurityException` sobe — e ela já tem tratamento próprio.

### 20.2 IDOR **latente** fechado no `ConnectorsController` (⚠️ severidade corrigida em 2026-07-19)

> ⚠️ **CORREÇÃO de um erro desta seção.** A primeira redação afirmava que o controller estava "ANÔNIMO" e "aberto a não-autenticados". **É FALSO.** `Program.cs` (~linha 89) define
> `options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()` — todo endpoint sem `[AllowAnonymous]` **já exigia autenticação**. Verificado ao vivo: a rota devolvia 401 anônima antes e depois. Não houve superfície anônima exposta em momento algum. O registro fica aqui para que a auditoria futura não herde uma severidade inflada.

O que de fato existia, e foi corrigido: o controller não tinha `[Authorize]` próprio, e a rota `tenants/{tenantId}/connectors/{connectorId}` carregava um `tenantId` que **parecia** governar autorização sem governar nada (quem isolava era o Global Query Filter). Dois defeitos reais:

- **Dependência de um default GLOBAL.** Sem `[Authorize]` local, qualquer mexida na `FallbackPolicy` abriria esta rota em silêncio. Garantia local > garantia herdada.
- **IDOR latente.** Um parâmetro de tenant que não decide nada convida a próxima pessoa a confiar nele — e a escrever o próximo endpoint filtrando por ele em vez de pelo contexto.

- Rota agora **`api/v1/connectors/{connectorId}`** + `[Authorize]`; o conector é resolvido pelo serviço DENTRO do tenant do JWT.
- **Id de outro cliente e id inexistente devolvem o MESMO 404** — a borda não confirma a existência de recurso alheio.
- O controller **não injeta mais o `AegisScoreDbContext`**: `GetConnectorAsync` resolve, `RecordSyncResultAsync` grava sinais + carimbo de sync numa transação só.
- **`LastStatus` deixou de mentir:** falha de coleta grava `Failed` e RELANÇA (o boundary global loga e responde). Antes era `Healthy` fixo.

### 20.3 Upsert do conector — invariante de banco, não promessa de código

`POST /tenants/connectors` era INSERT puro. Duplicatas de `(tenant, Provider, Capability)` quebravam dois consumidores: o `IConnectorRegistry` (resolve UM adaptador por par) e o `PolicyIngestionWorker` (projeta `(TenantId, Provider)` e sincronizaria a MESMA integração N vezes por ciclo).

- **Migration `20260719122213_UniqueConnectorConfigNaturalKey`** — índice ÚNICO `(TenantId, Provider, Capability)`. O EF suprimiu sozinho o `IX_Connectors_TenantId` (o composto é tenant-leading e cobre o prefixo).
- **Dedupe defensivo** antes do `CREATE UNIQUE INDEX`, no idioma da §2.6, com **duas diferenças**: (a) o sobrevivente é o **MAIS RECENTE** (`COALESCE(UpdatedAt, CreatedAt) DESC`), porque conectores duplicados carregam credenciais DIFERENTES e a última configuração é a intenção vigente — documentos duplicados são idênticos por hash, conectores não; (b) **`Signals.ConnectorConfigId` NÃO tem FK**, então os sinais do perdedor são REPONTADOS ao sobrevivente antes do DELETE — histórico de coleta é dado de auditoria e não pode evaporar num ajuste de índice.
- **Recuperação da corrida é DIFERENTE da do tenant:** provisionar tenant duas vezes é erro real (→ 409). *Configurar* um conector é **idempotente por intenção**, então o `DbUpdateException` do INSERT perdido **reconverge para UPDATE** sobre a linha vencedora (uma tentativa; re-SELECT vazio ⇒ não foi o índice natural, relança). Duas chamadas simultâneas convergem para uma linha e **nenhuma falha**.
- ⚠️ **Consequência de modelagem aceita:** um tenant não pode ter duas contas do MESMO provedor na mesma capacidade (ex.: dois M365 sob um cliente). Suportar isso exigiria discriminador de instância na chave, não este índice.

### 20.4 Segredo é escrita-apenas

- `ConnectorConfigDto` (Api) **não tem `EncryptedSettings`** — o segredo não atravessa a fronteira de saída, nem cifrado. Só o coletor o decifra.
- **Reconfigurar sem mandar segredo PRESERVA o vigente.** Rotação de credencial é ato explícito, não efeito colateral de quem só quis renomear o conector.
- ⚠️ **`Protect("")` devolve blob NÃO vazio** — gravá-lo faria o `TestAsync` dos conectores (que checa `IsNullOrWhiteSpace(EncryptedSettings)`) reportar *"credenciais presentes"* para um conector que nunca recebeu nenhuma. Na criação sem segredo grava-se `""`.

### 20.5 Outras mudanças de comportamento (aprovadas)

| Antes | Agora |
|---|---|
| `POST /tenants` → `Status = Active` | `Status = Onboarding` (o `AuthService` só barra `Suspended`, então login não trava) |
| slug cru do cliente | normalizado (trim + minúsculas) e validado `^[a-z0-9][a-z0-9-]{0,62}[a-z0-9]$` — 2–64 chars |
| `AnyAsync` → `Add` sem rede | fast-path + `catch (DbUpdateException)` → 409 (índice único de `Tenant.Slug`) |
| retorna `IdResponse` | retorna `ConnectorConfigDto` (**201** criou / **200** reconfigurou) |

⚠️ **Nenhuma dessas rotas é consumida pelo frontend** (verificado por varredura) — não há quebra de UI.

### 20.6 Testes — 22 casos novos (165 → 187)

`Tests/Tenancy/TenantManagementServiceTests.cs`, harness SQLite in-memory dos demais. Cobrem normalização/conflito/formato do slug, cifragem em repouso, upsert pela chave natural, preservação de segredo, piso do intervalo de sync, fail-closed sem tenant, isolamento cruzado (Get/Record de outro tenant) e a unicidade como invariante de banco.

⚠️ **Dois bugs REAIS pegos pelos testes durante esta sessão:**
1. A regex do slug tinha o grupo final **opcional**, aceitando slug de 1 caractere apesar do contrato dizer 2–64.
2. No refactor da reconvergência, `Apply(config, isInsert: true)` ficou **fixo** em vez de `isInsert: created` — apagava a credencial em TODA reconfiguração sem segredo. Era exatamente a regra de §20.4 que o teste existe para proteger.

⚠️ **`ConnectorConfig.TenantId` tem FK REAL para `Tenant`** (via a coleção `Tenant.Connectors`) — o fixture precisa semear as linhas de `Tenant`, senão o insert de conector morre com `FOREIGN KEY constraint failed`.

⚠️ **Cobertura honesta:** o ramo de reconvergência não tem cobertura DETERMINÍSTICA. O teste concorrente (`Task.WhenAll` sobre dois `DbContext`) valida a INVARIANTE (uma linha, nenhuma exceção), mas o SQLite serializa escritas — a corrida é exercitada oportunisticamente, não garantidamente.

### 20.7 ⚠️ Migrations são AUTO-APLICADAS no startup (fato do projeto — não redescobrir)

`Program.cs` (~linha 148) roda **`await db.Database.MigrateAsync()` em TODO boot**, antes de semear o catálogo. Consequências que valem registrar:

- **Não existe migration "pendente" na prática.** Basta subir a API para aplicar tudo que estiver no assembly. A `UniqueConnectorConfigNaturalKey` foi aplicada assim — pelo restart da API, **não** por um `dotnet ef database update` deliberado (o comando posterior devolveu *"No migrations were applied. The database is already up to date."*).
- ⚠️ **Deixar uma migration destrutiva "para revisão humana" NÃO funciona neste projeto.** Na §20.3 ela foi conscientemente não-aplicada porque o `Up()` contém `DELETE` de conectores duplicados; o próximo `dotnet run` a aplicou de qualquer forma. **Migration com efeito destrutivo precisa ter o SQL revisado ANTES do commit** — o gate humano depois do commit não existe.
- **Banco real de dev é `aegis_dev`**, não o `aegis` do `appsettings.json`: a connection string vem de `dotnet user-secrets` (o `Username` vazio no arquivo versionado denuncia o override). Confirmado no log do EF — *"Opening connection to database 'aegis_dev'"*.

---

## 21. Identidades — `IUserManagementService` sobre a fundação Um-para-Muitos

Provisionamento de usuários e concessão de acesso. **210/210** testes, build verde. **Nenhuma migration** — o domínio já bastava.

### 21.1 ⚠️ A contradição do enunciado e a decisão firmada

O enunciado pedia vínculo a "uma **lista** de `TenantIds`" (muitos-para-muitos) **e**, na mesma frase, "conforme definido no **Domínio**". O domínio é inequivocamente **Um-para-Muitos**: `User : ITenantOwned` com UM `Guid TenantId` não-nulável, sem entidade de junção, índice único `(TenantId, Email)`.

Muitos-para-muitos exigiria refatorar o NÚCLEO do isolamento: `JwtTokenService.CreateAccessToken` emite UMA claim `tenant_id` de `user.TenantId`; `AuthService.LoginAsync` acha o usuário **pelo query filter do tenant ambiente** (lookup cross-tenant não existe); `TenantConsistencyMiddleware` dá 403 em token sem `tenant_id` válido. **Decisão do Felipe (2026-07-19): manter Um-para-Muitos. Não refatorar o núcleo de auth, não afrouxar o middleware, não alterar o query filter de `User`.**

**A flag `IsGlobalAdmin` foi ABORTADA.** Usuário sem tenant é impossível por construção (`TenantId` é `Guid` não-nulável, `StampTenant` é fail-closed, `Guid.Empty` na claim leva 403). O papel `UserRole.PlatformAdmin` — que já existe — cobre o caso: cross-tenant por AUTORIZAÇÃO, ancorado a um tenant-sede, sem bypass de `TenantId`.

### 21.2 O que "mesmo e-mail em dois tenants" significa

**Duas identidades independentes**, não um usuário com dois vínculos: senha, papel, `IsActive` e refresh tokens próprios. O índice único é `(TenantId, Email)`, não `Email`. É isso que garante que **nenhum token atravesse a fronteira** — não existe sujeito capaz de "trocar de tenant".

⚠️ Consequência direta: `AssignUserToTenantAsync` **não pode escrever noutro tenant** (o `StampTenant` rejeitaria). Ele opera no tenant AMBIENTE, e o `TenantId` do comando é **asserção de defesa em profundidade** (idioma do `IControlStateWriter.ApplyVerdictAsync`) — divergiu, `TenantSecurityException`. Semântica: *"um TenantAdmin de B concede acesso a B"*, nunca *"um admin de A empurra alguém para B"*.

### 21.3 Superfície

- **Porta:** `Application/Services/IUserManagementService.cs`; **adapter:** `Infrastructure/Auth/UserManagementService.cs` (ao lado do `AuthService`, o outro dono do ciclo de vida da identidade). Scoped.
- `CreateUserAsync` — criação estrita no tenant ambiente. 409 se o e-mail já existe AQUI.
- `AssignUserToTenantAsync` — concessão IDEMPOTENTE: ausente → cria (exige `InitialPassword`); presente → aplica papel, **reativa** se inativa, e **preserva a senha vigente** (conceder permissão ≠ resetar credencial).
- **`UsersController` NOVO**, `[Authorize(Roles = "TenantAdmin")]` — ⚠️ deliberadamente **separado do `AuthController`**, que é `[AllowAnonymous]` (única superfície anônima da API). Pendurar criação de conta lá deixaria uma rota privilegiada dentro de um controller anônimo: um deslize de atributo viraria cadastro sem autenticação.
- `POST /api/v1/users` (201) · `POST /api/v1/users/access` (201 criou / 200 atualizou).

### 21.4 ⚠️ Escalonamento de privilégio barrado no serviço

`UserRole.PlatformAdmin` **não é atribuível** por esta superfície — nem no create, nem no update (a porta dos fundos). Ele autoriza criar tenants (§20), então emiti-lo por uma rota de tenant transformaria TenantAdmin em admin da PLATAFORMA com um POST. Devolve `RoleNotAssignable` → **403** (recusa de autorização, não de formato: 400 esconderia isso como erro de digitação). Provisionamento de PlatformAdmin segue fora do onboarding self-service, como o próprio enum documenta.

### 21.5 Política de senha — NIST SP 800-63B, sem regra de composição

**12–128 caracteres, e só.** O 800-63B desaconselha exigir maiúscula/dígito/símbolo: empurra o usuário para padrões previsíveis (`Senha@123`) sem ganho real de entropia. 12 é o piso (o 800-63B fixa 8 como mínimo absoluto) por ser console de administração de postura. Detalhes:

- **Nada é truncado** — truncar enfraqueceria a senha que o usuário acredita ter escolhido. Acima de 128 é recusa explícita.
- **A senha NÃO é trimada** — espaço é caractere legítimo, e aparar quebraria o login seguinte, que compara o valor cru. Senha só de espaços é recusada à parte.
- E-mail normalizado com a **mesma regra do `AuthService.LoginAsync`** (`Trim().ToLowerInvariant()`) — divergir aqui gravaria uma identidade que o login não acha.
- **Log sem e-mail:** o identificador auditável é o `UserId`. E-mail é PII e credencial; o `AuthService` também registra Tenant/User/TokenId, nunca o endereço.

### 21.6 Testes — 23 casos novos (187 → 210)

`Tests/Auth/UserManagementServiceTests.cs`. Além do caminho feliz e das validações, cobrem o que este serviço existe para garantir:

- **`MesmoEmailEmTenantsDistintos_SaoIdentidadesIndependentes`** — 2 linhas, hashes distintos, papéis distintos.
- **`CreateUserAsync_NaoEnxergaIdentidadeDeOutroTenant_AoChecarConflito`** — o 409 NÃO dispara cross-tenant; dispararia é que seria o bug (vazaria a existência de uma conta noutro cliente por canal lateral).
- **`AssignUserToTenantAsync_TenantDivergenteDoContexto_EhRecusado`** — `TenantSecurityException` antes de qualquer escrita.
- **`CreateUserAsync_PlatformAdmin_NaoEhAtribuivel`** + **`AssignUserToTenantAsync_NaoPromoveParaPlatformAdmin`** — os dois vetores de escalonamento.
- **`IndiceUnico_RejeitaSegundaIdentidadeComMesmoEmailNoTenant`** — insert cru: é o índice que barra, não o `if` do C#.
- **`CreateUserAsync_SenhaLongaSemComposicao_EhAceita`** — trava a regra do 800-63B contra alguém "endurecer" a política com composição no futuro.

---

## 22. SSO Simulado — `IdentityAccount` global, `User` como membership e o Tenant Switcher

O login deixou de exigir slug/tenant: o analista entra com **e-mail e senha** e alterna entre clientes por um dropdown no HUD, como numa plataforma de SOC terceirizado. **211/211** testes, `dotnet build` e `ng build` verdes, migration aplicada em `aegis_dev`.

### 22.1 ⚠️ Por que o enunciado literal era EXPLORÁVEL (não redescobrir)

O pedido original era: login por e-mail com `IgnoreQueryFilters()`, `GET /users/me/tenants` casando por e-mail, e `switch-tenant` validando "esse e-mail tem conta ativa no alvo?". **Sobre o modelo da §21 isso é bypass total de autenticação**, porque lá o mesmo e-mail em dois tenants eram identidades independentes **com hashes diferentes** — "e-mail → tenants" não era vínculo autenticado, era coincidência de string:

| # | Ação | Resultado |
|---|---|---|
| 1–2 | Mallory (TenantAdmin de um cliente pequeno) faz `POST /users` com `ceo@bancoX.com` e uma senha que ela escolhe | o serviço só validava FORMATO do e-mail, não posse |
| 3 | Login com esse e-mail + a senha dela | confere contra a linha do tenant DELA ✅ |
| 4–5 | `/users/me/tenants` → lista o bancoX; `switch-tenant` → "tem conta ativa lá?" → **sim** | JWT válido do bancoX |

O `TenantConsistencyMiddleware` não ajudaria: o token é internamente consistente. Havia ainda um bug funcional — "primeiro tenant encontrado" sem `ORDER BY` é não-determinístico, e quem tivesse senhas distintas por cliente teria login falhando de forma intermitente.

### 22.2 A correção: a credencial subiu para a PESSOA

- **`IdentityAccount` (NOVA, global):** `Email` (índice único **GLOBAL**), `PasswordHash`. **Não é `ITenantOwned`** — é o sujeito que ATRAVESSA tenants, e a única entidade de identidade com essa natureza.
- **`User` virou MEMBERSHIP:** mantém `TenantId` (segue `ITenantOwned`, query filter e stamping **intactos**), ganha `IdentityAccountId` (FK, `Restrict`) e mantém `Role`/`IsActive`/`DisplayName`/`LastLoginAt` — o que é POR cliente. `Email` e `PasswordHash` **saíram da tabela**.
- **Índice único mudou** de `(TenantId, Email)` para `(TenantId, IdentityAccountId)`.
- O vínculo virou **chave estrangeira**: quem não sabe a senha da pessoa não alcança ambiente nenhum dela. A cadeia da 22.1 morre no passo 3.
- **Trava correlata em `CreateUserAsync`:** se a conta JÁ existe, a senha informada é **DESCARTADA** — um TenantAdmin concede acesso ao PRÓPRIO tenant, nunca redefine credencial alheia. Teste: `CreateUserAsync_NaoPermiteTrocarASenhaDeContaExistente`.

⚠️ **Risco residual aceito:** um TenantAdmin ainda pode ADICIONAR um e-mail alheio ao próprio tenant (a pessoa passaria a ver aquele cliente no seletor). É incômodo/phishing, **não** vazamento — ele não obtém acesso ao ambiente dela. Um fluxo de convite com aceite fecharia isso (backlog).

### 22.3 Backfill — fusão por e-mail com eleição da senha mais recente

Migration `20260719174626_NormalizeIdentityAccount`. **Decisão do Felipe:** no MSSP o e-mail corporativo é a mesma pessoa física em todos os clientes ⇒ uma conta por e-mail DISTINTO, elegendo o hash de `COALESCE(UpdatedAt, CreatedAt)` mais recente (`DISTINCT ON`, PostgreSQL).

⚠️ **Consequência aceita:** a senha eleita passa a abrir ambientes que ela não abria, e as demais senhas daquele e-mail deixam de valer.

⚠️ **DOIS defeitos do scaffold do EF, corrigidos à mão** — o gerador é ordenador ingênuo, não migrador de dados:
- **`Up()` derrubava `Email`/`PasswordHash` ANTES de criar `IdentityAccounts`** — apagaria TODA credencial do sistema antes de haver para onde copiar. Ordem reescrita: criar destino → copiar → remover origem.
- **`Down()` tinha o defeito ESPELHADO** (dropava a tabela antes de devolver e-mail/hash às linhas) — rollback deixaria todo mundo com credencial em branco.
- `LOWER()` no agrupamento (linhas semeadas à mão podem ter caixa mista) + um bloco `DO $$` que **aborta a migration com mensagem clara** se sobrar linha órfã, em vez de estourar depois numa FK obscura.

### 22.4 Mecânica do SSO simulado

- **`account_id` é claim NOVA** no JWT: a PESSOA, estável através de ambientes. O `sub` continua sendo o MEMBERSHIP e **muda a cada troca**. `CreateAccessToken` passou a receber `(User membership, IdentityAccount account)`.
- **`POST /auth/login`** — valida contra `IdentityAccount` (sem filtro: ela não tem) e emite para o primeiro membership ativo, com **`ORDER BY CreatedAt, Id`** (ordem ESTÁVEL — sem isso o "primeiro ambiente" dependeria do plano do Postgres).
- **`GET /users/me/tenants`** — ancorado no `account_id` do token, nunca em e-mail do corpo. Só memberships ATIVOS de tenants não suspensos. Exige apenas `[Authorize]`, **não** TenantAdmin: todo analista vê os próprios acessos. *(Por isso o `[Authorize(Roles="TenantAdmin")]` do `UsersController` desceu da classe para as duas ações de escrita.)*
- **`POST /auth/switch-tenant`** — confirma membership ativo casando por `IdentityAccountId` (FK, não string), **revoga o refresh anterior** e emite par novo com o papel DAQUELE cliente. Devolve **403** e não 404 para não virar oráculo de existência de tenants.
- ⚠️ **`IgnoreQueryFilters()` ficou restrito à camada de auth** e SEMPRE ancorado num `IdentityAccountId` já autenticado. Nenhuma outra entidade é lida assim.

### 22.5 ⚠️ Duas armadilhas de framework que quase viraram falha

- **`[AllowAnonymous]` de CLASSE curto-circuita `[Authorize]` de método** no ASP.NET Core. Com ele no topo do `AuthController`, `switch-tenant` ficaria **aberta mesmo anotada**. O atributo desceu para as três ações que de fato são anônimas (login/refresh/logout).
- **Login e troca escrevem FORA do tenant ambiente** (no login não há tenant; na troca o ambiente é o ANTIGO) e o `StampTenant` fail-closed barraria — corretamente. **A saída NÃO foi afrouxar o carimbo:** `IssuePairAsync` abre um `AegisScoreDbContext` ligado ao tenant de DESTINO via `SystemTenantContext`, o mesmo padrão dos workers. `RefreshAsync` idem, ancorado no tenant que o próprio refresh token declara — o que faz o silent-refresh do F5 funcionar sem o cliente saber onde está.

### 22.6 Frontend — o ambiente ativo é DERIVADO do token

- **`environment.tenantId` foi REMOVIDO.** O `X-Tenant` do `authInterceptor` agora sai da claim `tenant_id` do próprio access token. Token e header vindo da MESMA fonte não podem divergir ⇒ o `TenantConsistencyMiddleware` nunca dispara por engano, e a troca vale para toda a API sem estado paralelo a sincronizar.
- `AuthService`: `getAvailableTenants()` / `switchTenant(id)`; signals `tenants`, e **`activeTenantId`/`activeTenant`/`activeRole` como `computed` do token** — deliberadamente NÃO editáveis: ambiente ativo como estado próprio do cliente poderia divergir do token e todo request levaria 403.
- **Quando a lista carrega:** no login (em PARALELO, para não atrasar a navegação) e no `restoreSession()` (F5). O `refresh()` de rotina **não** recarrega — os acessos da pessoa não mudam a cada 10 min.
- `TenantSwitcherComponent` montado no `.hud-topbar` do `app.component`. Some sozinho com ≤ 1 ambiente (`.hud-topbar:empty { display: none }` colapsa a faixa). Após trocar, faz `navigateByUrl('/')` + `window.location.reload()` — **recarga dura de propósito:** as telas montadas seguram dados do ambiente anterior e não podem seguir exibindo-os sob o novo rótulo.
- **Dívida limpa:** os 4 `'X-Tenant': environment.tenantId` hardcoded nos serviços foram removidos. Eram inofensivos (o `setHeaders` do interceptor sobrescreve), mas eram código morto enganoso.
- O componente de login **já era** só e-mail + senha — nada a mudar ali.

### 22.7 Correções de tradução do EF pegas SÓ em execução (não redescobrir)

O arco fechou com quatro falhas de tradução LINQ que **nenhuma checagem estática pega** — só rodar contra um banco real revela. Registradas porque a tentação de reescrever "mais elegante" traz todas de volta:

1. **Projetar direto num `record` dentro de `.Join()`** → *"The LINQ expression could not be translated"* (500 em `GET /users/me/tenants`, pego no smoke test ao vivo, não pelos testes). **Forma que traduz:** query syntax + projeção num tipo ANÔNIMO, montando o record em memória depois. Mesmo cuidado aplicado ao `ListConnectorsAsync` da §23.
2. **`ORDER BY` sobre `DateTimeOffset`** não é suportado pelo SQLite (provider da suíte). O `orderby u.CreatedAt` do login deixaria o caminho sem cobertura possível → ordenação movida para MEMÓRIA (custo nulo: uma pessoa tem um punhado de acessos).
3. **`ExecuteUpdate` + Global Query Filter** não traduz (`(Guid?)u.TenantId == __ef_filter__…`). Trocado por entidade RASTREADA + `SaveChangesAsync` em `IssuePairAsync` — que ainda economiza um round-trip, saindo no mesmo commit do refresh token.
4. **`ExecuteUpdate` + `IgnoreQueryFilters`** idem, na revogação do `SwitchTenantAsync`. Mesma correção.

⚠️ **A lição da §22.7:** a suíte não pegou (1) porque não havia teste do `AuthService` novo. `TenantSwitchingTests` foi criado DEPOIS, justamente para que a consulta seja EXECUTADA contra um banco relacional — falha de tradução acontece no provider, não no compilador.

---

## 23. Central de Integrações — a tela que conecta o Aegis aos ambientes reais

Primeira superfície de UI para os conectores: até aqui o backend tinha o upsert (§20.3) mas **não havia por onde inserir credencial**. `219/219` testes, `dotnet build` e `ng build` verdes.

### 23.1 ⚠️ Faltava o endpoint de LEITURA (a tela era impossível)

O `ConnectorsController` só tinha `POST test`/`sync`, e o `TenantsController` o `POST` de criação. **Nenhum `GET`.** Uma tela de gerenciamento que não lista o que está configurado fica em branco após um F5 — não gerencia nada.

- **`GET /api/v1/connectors` (NOVO)** → `IReadOnlyList<ConnectorConfigDto>`, tenant implícito no JWT.
- O `{connectorId}` **desceu da rota da CLASSE para as ações** (`[Route("api/v1/connectors")]` + `[HttpPost("{connectorId:guid}/test")]`), abrindo espaço para o `GET` na raiz. ⚠️ As URLs de `test`/`sync` **não mudaram**.
- Porta nova na Application: `ITenantManagementService.ListConnectorsAsync()` — sem parâmetro de tenant, como o resto (§20.1). Projeta em tipo ANÔNIMO no SQL (ver §22.7 item 1).

### 23.2 O segredo é escrita-apenas — `hasCredentials` é o que a UI recebe

`EncryptedSettings` **nunca** atravessa a fronteira de saída, nem cifrado. Mas a tela precisa distinguir *"configurado"* de *"cadastrado sem credencial"* — daí o booleano:

- `ConnectorSummary.HasCredentials` / `ConnectorConfigDto.hasCredentials` = `EncryptedSettings != ""`. É exatamente a checagem que o `TestAsync` dos conectores faz.
- ⚠️ **Correção de precisão durante a implementação:** a primeira versão derivava `hasCredentials` do que o CLIENTE enviou (`!IsNullOrWhiteSpace(req.Settings)`). Isso **mentiria numa reconfiguração sem segredo** — o upsert PRESERVA o vigente (§20.4), então renomear um conector reportaria "sem credencial". Passou a refletir o estado REAL após a escrita, calculado no serviço.
- ✅ **Verificado ao vivo:** segredo enviado no `settings` não aparece na resposta do `POST` nem na do `GET`.

### 23.3 Formulários reativos — ⚠️ dependência NOVA no Angular

**`@angular/forms@19.2.25` foi ADICIONADO.** O projeto nunca teve o pacote: o `login.component` usa template refs justamente por isso ("Sem @angular/forms de propósito"). Ele entrou por pedido explícito de formulários reativos, e **introduz um padrão novo** — as demais telas seguem signal-first sem forms.

- `pages/integrations.component.ts` — `FormGroup` com subgrupo `credentials` **RECONSTRUÍDO a cada troca de provedor**: o catálogo diz quais campos cada um exige.
- `models/connector.models.ts` — `PROVIDERS` mapeia provedor → `{ authType, capability, fields[] }`, com `secret: true` nos campos que viram input mascarado (+ botão Mostrar/Ocultar). ⚠️ **Os enums viajam como INTEIRO** no POST e voltam como STRING no GET; o catálogo carrega os dois lados.
- **Por que não um textarea de JSON:** seria trivial de codificar e péssimo de operar. Quem configura um Sentinel às 3h precisa de rótulos, não de sintaxe.
- Após salvar, o subgrupo de credenciais é **reconstruído** — o segredo não fica no DOM, e a tela nunca o recebe de volta para repopular.
- `services/connector.service.ts` — **nenhum método recebe TenantId** (§20/§22). Traduz os códigos HTTP que estas rotas realmente emitem, incluindo **501** = sem adaptador registrado para o par provider+capability (situação REAL hoje: só `Microsoft/SecureScore` tem adaptador; o "Testar" num Sentinel devolve 501 legitimamente).
- Rota `/settings/integrations` + grupo "Configuração" no sidebar.

### 23.4 Lacunas conscientes

- **Sem `DELETE` e sem toggle de `Enabled`** no backend ⇒ a tela cria e reconfigura, mas desativar uma integração ainda exige SQL.
- ⚠️ **O catálogo de provedores é uma CONVENÇÃO, não um contrato.** Os campos de Sentinel/SecOps/CrowdStrike/AWS/Splunk foram definidos por convenção de cada API. O backend **não valida** esse JSON — só cifra e guarda —, então um campo com nome errado só apareceria como falha na primeira coleta real. Conferir contra o que cada conector for consumir.
- **Sem teste automatizado da tela** — o projeto não tem suíte de frontend (só `ng build`). O `ListConnectorsAsync` também ficou sem teste unitário; foi validado por smoke test ao vivo (criação → 201, upsert → 200 com 1 linha, credencial preservada, segredo ausente das respostas).
