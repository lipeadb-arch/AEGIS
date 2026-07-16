# AEGIS_STATE.md — Snapshot Arquitetural Tático

> **Propósito deste arquivo:** reinjeção de contexto exato em futuras sessões de IA, sem reler o código-fonte. Não é documentação comercial. Última atualização: **2026-07-14**.
>
> **Base de versionamento:** todo o trabalho das últimas sessões (Identify → Recover + frontend vivo + Copiloto GRC + ID.RA/Raio de Explosão + **Ingestão de Identidade do Entra ID ponta a ponta — telemetria multi-controle + controle compensatório OT + tela HUD + sinais tipados PR.DS/PS/IR + humanização das siglas NIST, validado ao vivo**) está no **working tree, NÃO commitado**, por cima do commit `dcedf57` (branch `feat/telemetry-ingestion-scoring-consolidation`). O Felipe versiona manualmente.

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

### 2.5 Testes — **110/110 verdes** (build da solução verde, 0 erros, em 2026-07-13)

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

**Onde paramos:** backend NIST completo; **110/110 testes**. **Pilar Protect FECHADO**: PR.AA (identidade Entra + compensação OT) e PR.DS/PR.PS/PR.IR com **sinais tipados** na Application (ver 2.11) e **cobertura de teste** — ⚠️ as 3 regras PR.DS/PS/IR já existiam no `EvaluateProtect` (nada mudou no Stub). **Ingestão de Identidade do Entra ID — COMPLETA e VALIDADA AO VIVO ponta a ponta** (ver 2.10 + 4.6): contratos multi-controle → provider stub → `IdentityTelemetryController` (POST /telemetry/identity/entra-id, reusa a esteira) → **controle compensatório OT** (3 vias em PR.AA-01) → tela HUD `/identity` com toggle de isolamento (demo ao vivo: NonCompliant→Mitigado + badge "COMPENSATED CONTROL") + "Auditar Lacunas" → Copiloto (START_INTERVIEW). **ID.RA (Raio de Explosão) — COMPLETO e VALIDADO AO VIVO** (motor Dijkstra → hook no ledger → Generative UI `blast-radius-graph`, ver 2.9/4.4). **Humanização das siglas NIST** (glossário PT-BR no card + pilares, ver 4.7). Provider Pattern documental + `/sync` prontos (SharePoint STUB). Frontend: 4 painéis de pilar + Govern + Copiloto (Agentic Routing + entrevista GRC) + tela de Identidade; a casca não vaza no `/login` (ver 4.5). **Nada commitado** (Felipe versiona manualmente).

**Próximos passos imediatos (prioridade):**

1. ~~**ID.RA — Raio de Explosão completo ponta a ponta**~~ ✅ **CONCLUÍDO e VALIDADO AO VIVO** — motor + hook + migration + endpoint + **seed de topologia** + **Generative UI** (`blast-radius-graph`, ver 4.4); smoke test E2E ok (ver 2.9). **Refinamentos futuros:** (a) adicionar a intenção **BLAST_RADIUS ao Agentic Routing** do backend (hoje o gatilho é frontend por palavra-chave); (b) **enriquecer o `BlastRadiusResponseDto`** com o NOME dos ativos (a UI mostra Guids curtos); (c) limiares do hook (severidade/alcance) configuráveis via `RiskAppetite`.
1. ~~**Reativar a Entrevista GRC no Angular via Agentic Routing**~~ ✅ **CONCLUÍDO** (ver 2.5 + 4.3) — o `auditor-chat` trata `Intent === "START_INTERVIEW"` via união discriminada + `@switch` e injeta o `grc-question-card`, que consome `/governance/interviews` (start/answer/outcomes) e publica no barramento. *(Pendência fina: os outcomes ainda saem do `StubAssessmentService.SuggestMaturity` canned — ligar ao motor real cai no item 3.)*
1. ~~**Ingestão de Identidade do Entra ID (telemetria multi-controle + compensação OT + tela HUD)**~~ ✅ **CONCLUÍDO e VALIDADO AO VIVO** (ver 2.10 + 4.6). **Refinamentos futuros:** (a) `EntraIdTelemetryProviderStub` → OAuth client credentials + Microsoft Graph real; (b) no motor real, exigir `PrivilegedAccountsWithoutMfa <= MfaExemptServiceAccounts` p/ não mascarar admin HUMANO atrás do isolamento OT; (c) **enriquecer o response com findings por INDICADOR** (hoje a tabela mostra 1 linha por controle NIST — o modelo do frontend já suporta N).
1. ~~**Fechar o Protect (PR.DS/PS/IR) + humanizar as siglas NIST**~~ ✅ **CONCLUÍDO e VALIDADO AO VIVO** (ver 2.11 + 4.7): sinais tipados Data/Platform/Network + 6 testes (lacuna fechada) + glossário PT-BR no card/pilares. **Refinamento opcional:** unificar as rotas `/protect/{data,platform,network}` para consumirem os signals (hoje usam os DTOs da Api mais ricos — Dlp/AppLocker/Microsegmentation), só se quisermos eliminar a composição inline.
2. **Conectores Microsoft ainda são STUB** — `SharePointProvider.cs` (documentos, falta OAuth client credentials + `GET /sites/{id}/drive/root/children`) **e** `EntraIdTelemetryProviderStub.cs` (identidade, falta OAuth + Graph directoryRoles/authenticationMethods/signInActivity). Ambos com o Provider Pattern JÁ pronto; segredos em `ConnectorConfig.EncryptedSettings`.
3. **Ligar o motor real de IA** — `GeminiLlmClient` (`gemini-2.0-flash`) e `ClaudeAssessmentService` (Anthropic, `claude-sonnet-5`). ⚠️ **Testado ao vivo em 2026-07-13: o Gemini real respondeu HTTP 429 (quota do Felipe esgotada) → `AiUnavailableException`/503** — as provas E2E usam os Stubs, forçados subindo a API com `AegisAi__ApiKey=' '`/`Ai__ApiKey=' '` (ESPAÇO: no PowerShell `''` REMOVE a env var; um espaço passa `IsNullOrWhiteSpace`=true → o DI cai no Stub). O Copiloto GLOBAL ainda não injeta a postura real do tenant no prompt (hoje só persona por escopo) — hook para as queries de scoring.
4. **HUD `/trend`** só preenche via `AegisScoreSnapshotWorker` (foto à meia-noite UTC); considerar snapshot no boot em DEV.

**Ambiente de execução (DEV):** API em `http://localhost:5100` (`dotnet run --launch-profile http`). ⚠️ Banco real (via `dotnet user-secrets`) é **`aegis_dev`** — diverge do `Database=aegis` do `appsettings.json` versionado (DEV.md Passo 1). DemoTenant `aa000000-0000-0000-0000-000000000001`; usuário `analista@demo.aegis` / `Aegis@12345` (via `POST /dev/seed-user`). ⚠️ O **login (`POST /auth/login`) exige o header `X-Tenant`** — multi-tenant resolve o usuário dentro do tenant. Segredos (JWT, connection string, `AegisAi:ApiKey`, `Ai:ApiKey`) em `dotnet user-secrets` — ver `DEV.md`. **Schema aplicado no boot** (`db.Database.MigrateAsync()` em `Program.cs`) — não rodar `dotnet ef database update` à mão em uso normal. Frontend: `npm --prefix <frontend> run build` (ou `ng serve`); `environment.tenantId` = DemoTenant. ⚠️ Ao rodar `dotnet build` da solução com a API ativa, o `bin` do Api fica travado (MSB3026) — parar a API ou compilar o Api isolado (`--no-dependencies -o <tmp>`).

⚠️ **CORS (dev):** a política `aegis-spa` (`Program.cs`) só libera `http://localhost:{5173,5273,3000}` — rode o `ng serve` em **`--port 5173`** (a porta padrão 4200 é bloqueada). **Raio de explosão demo:** `DevController.DemoRootAssetId = bb000000-0000-0000-0000-000000000001` (o AD DC), espelhado em `environment.blastRadiusDemoAssetId`. **Motor de IA real (trocar o Stub):** `dotnet user-secrets set "Ai:ApiKey" "sk-ant-…" --project src/AegisScore.Api` — chave Anthropic; o DI (`AddAegisScoreInfrastructure`) troca o `StubAssessmentService` pelo `ClaudeAssessmentService` (`claude-sonnet-5`) no próximo boot. O `StubLlmClient`/`GeminiLlmClient` seguem a MESMA lógica com `AegisAi:ApiKey`. **Demo de identidade (Entra ID):** a tela `/identity` foi validada em `ng serve --port 5273`; o toggle "Rede Isolada (OT)" re-avalia e demonstra a compensação ao vivo (PR.AA-01 NonCompliant→Mitigado).

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
