# AEGIS_STATE.md — Snapshot Arquitetural Tático

> **Propósito deste arquivo:** reinjeção de contexto exato em futuras sessões de IA, sem reler o código-fonte. Não é documentação comercial. Última atualização: **2026-07-13**.
>
> **Base de versionamento:** todo o trabalho das últimas sessões (Identify → Recover + frontend vivo + Copiloto GRC + **ID.RA/Raio de Explosão ponta a ponta, validado ao vivo**) está no **working tree, NÃO commitado**, por cima do commit `dcedf57` (branch `feat/telemetry-ingestion-scoring-consolidation`). O Felipe versiona manualmente.

---

## 1. Visão Geral e Propósito

**Aegis Score** é uma plataforma de **Secure Score corporativo** baseada no **NIST CSF 2.0**. Traduz evidência técnica real (telemetria de SOC multicloud + documentos de governança) num score de postura por controle NIST, agregável por Função/Categoria (modelo Microsoft Secure Score).

- **Backend:** .NET 10 + PostgreSQL, Clean Architecture (`AegisScore.{Api, Application, Domain, Infrastructure, Connectors.Microsoft}`).
- **Frontend:** Angular 19 (standalone + signals), tema HUD dual-neon.
- **Multitenancy Secure-by-Design:** EF Core Global Query Filters + stamping fail-closed no `SaveChanges` (`ITenantOwned`), tenant resolvido do claim `tenant_id` do JWT (`HttpTenantContext`), nunca de header spoofável. `TenantConsistencyMiddleware` barra divergência token×header (403).
- **Duas fontes de evidência, um único ledger** (`TenantControlState`, célula tenant×subcategoria):
  - **Telemetria** (`IAegisAiEvaluatorService.EvaluateAsync`) — AUTORITATIVA, pode levar a 100%. **Agora cobre também Govern** (GV.SC/GV.RR — ver 2.7).
  - **Documental** (`DocumentAnalysisWorker`, Govern) — teto de 50% (`MitigatedByThirdParty`).
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
| **ID** Identify | `POST /api/v1/telemetry/asset` (ID.AM) · **`POST /api/v1/risk-assessment/{assetId}/blast-radius`** (ID.RA — Raio de Explosão, ver 2.9) | ID.AM-01 · **ID.RA-01/05 (via hook do raio)** |
| **PR** Protect | `POST /api/v1/telemetry/protect/{identity,data,platform,network}` | PR.AA-01, PR.DS-01, PR.PS-01, PR.IR-01 |
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

`ILLMClient` determinístico usado quando **não há `AegisAi:ApiKey`**. Faz **parsing numérico real via regex** (helpers `Num(label)` e `Flag(label)` sobre o payload lowercased), não só keyword-matching. Roteia por família de rótulos: `EvaluateProtect` → `EvaluateDetect` → `EvaluateRespondRecover` → **`EvaluateGovern`** → genérico. ⚠️ `EvaluateGovern` VEM ANTES do fallback genérico de propósito: o rótulo `"Third Party Audited:"` contém `"third party"`, que o genérico casaria como `MitigatedByThirdParty` e mascararia o veredito real de GV.SC. Cada categoria é **binária** (falha em qualquer condição = `NonCompliant`; passa em tudo = `Compliant`). Motor real: `GeminiLlmClient` (modelo **`gemini-2.0-flash`**; falha HTTP → `AiUnavailableException`/503).

### 2.5 Testes — **100/100 verdes** (build da solução verde, 0 erros, em 2026-07-12)

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

O catálogo de teste semeia PR.AA-01, DE.CM-01, RS.MA-01, RS.MI-01, RC.RP-01, ID.AM-01, **GV.SC-01 (peso 15) e GV.RR-01 (peso 5)**. *(O Agentic Routing (2.8) AGORA tem cobertura dedicada — os dois arquivos acima; os testes de assessment não tocam banco: são roteamento puro + transporte fake.)*

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
| **GV.SC-01** | Supply Chain | `SuppliersWithNetworkAccess > 0` **e** `!ThirdPartyAudited` — *fornecedor com acesso à rede sem auditoria de terceiros* |
| **GV.RR-01** | Roles & Auth. | `AdminAccountsWithoutReview > 0` **ou** `!PrivilegedAccessReviewConfigured` — *autoridade sem accountability* |

*(GV.PO segue avaliado por documento — PDF → teto 50%. GV.SC/GV.RR agora têm telemetria autoritativa, ver 2.7.)*

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
- **Já era global** (montado no `app.component` dentro de `<app-drawer>`; o `document-hub` só tem o botão de abrir).
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

---

## 5. Ponto de Parada e Backlog

**Onde paramos:** backend NIST completo; **100/100 testes** (Agentic Routing + motor de raio + hook de score; dedupe por índice único). **ID.RA (Raio de Explosão) — COMPLETO e VALIDADO AO VIVO ponta a ponta**: domínio → motor Dijkstra → orquestrador → EF (migration aplicada) → HTTP → hook no ledger → **Generative UI** no chat (`blast-radius-graph` via `@case`, ver 4.4). O smoke test E2E confirmou a degradação matemática e a penalização de ID.RA-01/05 no `TenantControlState` (ver 2.9); seed de topologia demo pronto. Provider Pattern documental + `/sync` prontos (SharePoint STUB). Frontend com os 4 painéis de pilar **vivos**, Govern refatorado, Copiloto GRC com Agentic Routing + entrevista GRC reativada (`grc-question-card`), e a casca não vaza mais no `/login` (ver 4.5). **Nada commitado** (Felipe versiona manualmente).

**Próximos passos imediatos (prioridade):**

1. ~~**ID.RA — Raio de Explosão completo ponta a ponta**~~ ✅ **CONCLUÍDO e VALIDADO AO VIVO** — motor + hook + migration + endpoint + **seed de topologia** + **Generative UI** (`blast-radius-graph`, ver 4.4); smoke test E2E ok (ver 2.9). **Refinamentos futuros:** (a) adicionar a intenção **BLAST_RADIUS ao Agentic Routing** do backend (hoje o gatilho é frontend por palavra-chave); (b) **enriquecer o `BlastRadiusResponseDto`** com o NOME dos ativos (a UI mostra Guids curtos); (c) limiares do hook (severidade/alcance) configuráveis via `RiskAppetite`.
1. ~~**Reativar a Entrevista GRC no Angular via Agentic Routing**~~ ✅ **CONCLUÍDO** (ver 2.5 + 4.3) — o `auditor-chat` trata `Intent === "START_INTERVIEW"` via união discriminada + `@switch` e injeta o `grc-question-card`, que consome `/governance/interviews` (start/answer/outcomes) e publica no barramento. *(Pendência fina: os outcomes ainda saem do `StubAssessmentService.SuggestMaturity` canned — ligar ao motor real cai no item 3.)*
2. **`SharePointProvider` ainda é STUB** (`Connectors.Microsoft/SharePointProvider.cs`) — o Provider Pattern JÁ EXISTE; falta autenticação OAuth client credentials (segredos em `ConnectorConfig.EncryptedSettings`) + `GET /sites/{id}/drive/root/children` real.
3. **Ligar o motor real de IA** — `GeminiLlmClient` (`gemini-2.0-flash`) e `ClaudeAssessmentService` (Anthropic, `claude-sonnet-5`) **não testados contra os provedores** (quota/chave do Felipe); a prova ao vivo usa os Stubs (sem `AegisAi:ApiKey`/`Ai:ApiKey`). O Copiloto GLOBAL ainda não injeta a postura real do tenant no prompt (hoje só persona por escopo) — hook para as queries de scoring.
4. **HUD `/trend`** só preenche via `AegisScoreSnapshotWorker` (foto à meia-noite UTC); considerar snapshot no boot em DEV.

**Ambiente de execução (DEV):** API em `http://localhost:5100` (`dotnet run --launch-profile http`). ⚠️ Banco real (via `dotnet user-secrets`) é **`aegis_dev`** — diverge do `Database=aegis` do `appsettings.json` versionado (DEV.md Passo 1). DemoTenant `aa000000-0000-0000-0000-000000000001`; usuário `analista@demo.aegis` / `Aegis@12345` (via `POST /dev/seed-user`). Segredos (JWT, connection string, `AegisAi:ApiKey`, `Ai:ApiKey`) em `dotnet user-secrets` — ver `DEV.md`. **Schema aplicado no boot** (`db.Database.MigrateAsync()` em `Program.cs`) — não rodar `dotnet ef database update` à mão em uso normal. Frontend: `npm --prefix <frontend> run build` (ou `ng serve`); `environment.tenantId` = DemoTenant. ⚠️ Ao rodar `dotnet build` da solução com a API ativa, o `bin` do Api fica travado (MSB3026) — parar a API ou compilar o Api isolado (`--no-dependencies -o <tmp>`).

⚠️ **CORS (dev):** a política `aegis-spa` (`Program.cs`) só libera `http://localhost:{5173,5273,3000}` — rode o `ng serve` em **`--port 5173`** (a porta padrão 4200 é bloqueada). **Raio de explosão demo:** `DevController.DemoRootAssetId = bb000000-0000-0000-0000-000000000001` (o AD DC), espelhado em `environment.blastRadiusDemoAssetId`. **Motor de IA real (trocar o Stub):** `dotnet user-secrets set "Ai:ApiKey" "sk-ant-…" --project src/AegisScore.Api` — chave Anthropic; o DI (`AddAegisScoreInfrastructure`) troca o `StubAssessmentService` pelo `ClaudeAssessmentService` (`claude-sonnet-5`) no próximo boot. O `StubLlmClient`/`GeminiLlmClient` seguem a MESMA lógica com `AegisAi:ApiKey`.
