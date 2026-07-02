# Aegis Score — Auditoria de Maturidade Cibernética
### Arquitetura & Design Técnico — v0.1
Módulo de auditoria de maturidade cibernética do Portal **Synapse OS** · parceria conceitual com a tese GRC (risklab + Perinity) · fundamentado em **NIST CSF 2.0** e **GRC**.

---

## 1. Objetivo

Aegis Score é um **motor de IA para diagnóstico contínuo de maturidade cibernética** em ambientes corporativos multi-cliente. Ele substitui o processo manual de auditoria (planilhas + questionários de autodeclaração + entrevistas) por um ciclo automatizado que:

1. aplica o framework **NIST CSF 2.0** (6 Funções, 22 Categorias, 106 Subcategorias) como espinha dorsal;
2. coleta **evidências de duas naturezas** — documentais (políticas, procedimentos) analisadas por IA, e **fatos concretos** coletados por API nas ferramentas do cliente (Microsoft, Google, AWS, SIEMs, EDRs);
3. classifica a **maturidade** de cada processo (escala CMMI 1–5), calcula o **gap** entre estado atual e alvo, registra **riscos**, mede **efetividade de controles** e gera **planos de ação e relatórios executivos priorizados** de forma autônoma;
4. opera de forma **contínua** — o diagnóstico reflete a realidade do ambiente, não uma foto estática.

A frase-guia da tese GRC resume o propósito do produto: o risco cibernético só vira decisão quando *vulnerabilidade, ativo, processo, controle, compliance e auditoria falam a mesma língua*. Aegis Score é a camada de tradução que produz essa língua comum.

---

## 2. Problema atual → o que Aegis Score muda

| Hoje (modelo manual) | Com Aegis Score |
|---|---|
| Questionários de autodeclaração (Sim/Não/N/A) sem validação | Autodeclaração **confrontada** com evidência documental (IA) e fatos de API |
| Maturidade pontuada à mão em planilha por analista | Maturidade **inferida/sugerida** pela IA, revisada pelo analista (human-in-the-loop) |
| Diagnóstico envelhece no dia seguinte | **Monitoramento contínuo**: re-coleta e re-pontuação automáticas |
| Risco técnico (CVE/CVSS) desconectado do negócio | Cadeia **Vulnerabilidade → Ativo → Processo → Risco Corporativo → Controle** |
| Relatório de volume ("42 vulnerabilidades críticas") | Relatório de **impacto e exposição** + plano de ação priorizado (Score ICR) |
| Esforço de semanas por área | Coleta e pré-análise em minutos; analista foca em validação e decisão |

Aegis Score não substitui o julgamento do analista nem mistura as áreas: é um **validador** entre a diretriz teórica e a prática operacional, **preservando a independência das linhas de defesa** (ver §3).

---

## 3. Princípios de arquitetura

1. **Framework-agnóstico no núcleo, NIST-first na entrega.** O catálogo de controles é dado versionado e seedável; hoje carregamos NIST CSF 2.0, amanhã ISO 27001/CIS sem refatorar o domínio.
2. **Stack-agnóstico via Adapter + Facade.** O núcleo *nunca* fala a língua nativa de um fornecedor (Microsoft, Google, CrowdStrike): ele opera sobre um **esquema JSON unificado** (`EvidenceSignal`) — a "língua universal" da aplicação. Cada ferramenta entra por um **Adapter** (plugin) atrás de uma **Facade** única (`IEvidenceConnector`), que traduz a API/saída nativa para esse esquema. Adicionar AWS/Google/Splunk/SentinelOne = adicionar um adapter, configurado no **onboarding** — sem tocar no Core.
3. **Evidência é cidadã de primeira classe.** Toda pontuação de maturidade é rastreável a evidências (documento, sinal de API, resposta, screenshot). Sem evidência, a IA marca baixa confiança e escala para o humano.
4. **Human-in-the-loop por padrão.** A IA sugere; o analista valida. Toda sugestão carrega `confidence`, `rationale` e `evidenceRefs`.
5. **Independência das linhas de defesa.** Segurança (1ª linha), Riscos & Compliance (2ª linha) e Auditoria (3ª linha) compartilham **dados**, não responsabilidades. O modelo registra origem e dono de cada artefato; trilha de auditoria imutável.
6. **Multi-tenant desde o dia zero.** Isolamento lógico por `TenantId` em todas as entidades operacionais; segredos de conector criptografados por tenant.
7. **LLM-agnóstico.** O motor de IA é uma interface (`IAiAssessmentService`); a implementação pode ser Claude (Anthropic), Azure OpenAI, ou modelo local — trocável por configuração.
8. **Aberto a melhorias.** Camadas limpas (Domain → Application → Infrastructure → API), contratos versionados, conectores e regras de pontuação plugáveis.

---

## 4. Capacidades funcionais (módulos)

1. **Framework Catalog** — NIST CSF 2.0 versionado (Funções/Categorias/Subcategorias, Implementation Examples, Informative References, escala de maturidade).
2. **Assessment Campaigns** — orquestra a campanha por *Processo × Área/BU* (a hierarquia do Plano Diretor) e o workflow de tasks.
3. **Questionnaire Engine** — questionários por subcategoria, com *orientação de resposta* em linguagem de negócio; aplicação self-service e/ou condução por IA conversacional.
4. **Evidence Engine** — ingestão documental (IA/RAG) + coleta ativa por API (conectores) → sinais normalizados.
5. **Maturity Engine** — classifica nível 1–5 por subcategoria (Atual/Alvo), agrega para Categoria/Função/Geral, calcula gaps.
6. **Risk Engine** — registro de riscos (ameaça × vulnerabilidade × processo), avaliação (Prob × Impacto × Valor), tratamento, risco residual, matriz e apetite.
7. **ICR Engine** — Índice de Criticidade de Risco Cibernético (0–100), ponderado e contínuo (tese Perinity).
8. **Action Plan & Reporting** — planos de ação priorizados + relatórios executivos (Plano Diretor automatizado) gerados por IA.
9. **Dashboards** — visão executiva (a exemplo dos prints): radar de maturidade, gap atual×alvo, heatmap de risco, efetividade de controles, exposição, compliance.
10. **Connector & Onboarding** — cadastro de stacks/ferramentas do cliente, credenciais, mapeamento sinal→subcategoria.
11. **Tenancy, Auth & Audit** — multi-cliente, RBAC, trilha de auditoria.

---

## 5. Modelo de domínio

O modelo abaixo é derivado **diretamente dos seus artefatos**: o workbook de maturidade NIST (`Requirements` + `Maturity Definitions`), o questionário por BU (SAQ), o sistema de gestão de riscos (`Identificação`/`Avaliação`/`Plano de Ação`/`Risco Residual`/`Matriz`) e a hierarquia do board deck (Processo → Assessment por Área → Tasks).

### 5.1 Tenancy & contexto organizacional
- **Tenant** *(Cliente)* — `Id, Name, Slug, Status, CreatedAt`
- **BusinessUnit** *(Área/BU)* — `Id, TenantId, Name, Code, ManagerName, ManagerEmail` *(ex.: BU "Compliance", gestor "João Neto Silva")*
- **BusinessProcess** *(Atividade Principal)* — `Id, TenantId, Name, ProcessCategory, Classification (Público|Interno|Confidencial|Restrito), ProcessValue (1–4)` *(ex.: "Gestão de Identidade e Acesso", "Operações de Segurança", "Confidencial")*
- **Asset** *(Ativo Digital)* — `Id, TenantId, Name, Type, Criticality (1–4), OwnerName, BusinessProcessId?, ExternalRef (CMDB id)` — elo da cadeia Vulnerabilidade→Ativo→Processo.

### 5.2 Framework (referência, versionado, seedado)
- **FrameworkVersion** — `Id, Name ("NIST CSF 2.0"), Source, IsActive`
- **NistFunction** — `Id, FrameworkVersionId, Code (GV), Name, Definition, Order`
- **NistCategory** — `Id, FunctionId, Code (GV.OC), Name, Definition`
- **NistSubcategory** — `Id, CategoryId, Code (GV.OC-01), Description, ImplementationExamples, InformativeReferences (jsonb)`
- **MaturityLevel** — `Id, FrameworkVersionId, Level (1–5), Name, Description, Score` *(Performed→Documented→Managed→Quantitatively Managed→Optimizing)*

> Seed pronto: `data/nist_csf_2_0_catalog.json` (6/22/106 + 5 níveis), extraído do seu próprio workbook.

### 5.3 Avaliação (Assessment)
- **Assessment** *(campanha)* — `Id, TenantId, FrameworkVersionId, Name, Status (Draft|InProgress|InReview|Published), StartDate, EndDate`
- **AssessmentScope** *(Macroatividade `[Assessment] – Processo – Área`)* — `Id, AssessmentId, BusinessProcessId, BusinessUnitId, Status`
- **AssessmentTask** *(workflow do Plano Diretor)* — `Id, AssessmentScopeId, Type (Kickoff|SendQuestionnaire|FollowUp|ValidateAnswers|EvaluateMaturity|Interview|PresentResults), Status, AssigneeId, DueDate`
- **Question** — `Id, SubcategoryId, ThemeGroup (ex.: "INVENTÁRIO", "SOFTWARE E NUVEM"), Text, Guidance (orientação de resposta), Order, AnswerType (YesNoNa|Scale|Text)`
- **Answer** — `Id, AssessmentScopeId, QuestionId, Value, Comment, Source (SelfDeclared|AiInferred|ApiValidated), RespondedById, RespondedAt`
- **Evidence** — `Id, TenantId, AssessmentScopeId?, SubcategoryCode?, Type (Document|Link|ApiSignal|Screenshot|Interview), Uri, BlobRef, Source, AiSummary, CollectedAt, Hash`
- **SubcategoryEvaluation** *(o "coração" — uma linha `Requirements`)* — `Id, AssessmentScopeId, SubcategoryId, CurrentLevel (1–5), CurrentScore, CurrentComments, TargetLevel, TargetScore, TargetComments, EvaluatedBy (Analyst|Ai), Confidence (0–1), Rationale, EvidenceRefs (jsonb), ReviewedById?, ReviewedAt?`

### 5.4 Conectores & sinais
- **ConnectorConfig** — `Id, TenantId, Provider (Microsoft|Google|Aws|MicrosoftSentinel|CrowdStrike|Splunk|...), Capability (SecureScore|DefenderExposure|PurviewCompliance|AzureAdvisor|ConfigAnalyzer|Siem|Edr|Cmdb), DisplayName, AuthType (OAuthClientCredentials|ApiKey|...), EncryptedSettings (jsonb), Enabled, SyncIntervalMinutes, LastSyncAt, LastStatus`
- **EvidenceSignal** *(fato coletado)* — `Id, TenantId, ConnectorConfigId, SignalKey (ex.: "secureScore.identity"), NumericValue?, JsonValue (jsonb), Unit, Severity, MappedSubcategoryCodes (jsonb), CollectedAt`
- **SignalMapping** *(regra sinal→subcategoria)* — `Id, FrameworkVersionId, Capability, SignalKey, SubcategoryCodes (jsonb), Weight, ScoringHint` — define como um fato (ex.: Secure Score Identity 67%) influencia a maturidade sugerida de subcategorias (ex.: PR.AA-*).

### 5.5 Riscos
- **Risk** — `Id, TenantId, Code (SEC0001), Title, Description, BusinessProcessId, BusinessUnitId, Threats, Vulnerabilities, FocalPoint, ManagerName, Classification, RegisteredAt, OriginSubcategoryCode?, OriginEvidenceId?` *(origem rastreável a um gap de maturidade ou a um sinal de conector)*
- **RiskEvaluation** — `Id, RiskId, Phase (Inherent|Residual), ProcessValue (1–4), Probability (1–4), Impact (1–4), RiskScore, RiskLevel (Baixo|Médio|Alto|Crítico), EvaluatedAt`
- **RiskTreatment / ActionPlan** — `Id, RiskId, Treatment (Aceitar|Mitigar|Transferir|Evitar), Description, ResponsibleArea, ResponsiblePerson, HowToImplement, StartDate, DueDate, Status (Aberto|EmAndamento|Concluido|Vencido), CompletedAt`
- **RiskAppetite** *(por tenant)* — `Id, TenantId, ThresholdsJson` — faixas e limite de apetite.

### 5.6 Pontuação (snapshots/derivados)
- **MaturitySnapshot** — `Id, AssessmentId, Level (Overall|Function|Category|Subcategory|Scope), RefCode, CurrentScore, TargetScore, Gap, ComputedAt`
- **IcrScore** — `Id, TenantId, SubjectType (Vulnerability|Asset|Risk|Process), SubjectRef, Score (0–100), Band (Controlado|Moderado|Alto|Critico), FactorsJson, ComputedAt`

---

## 6. Metodologia de avaliação

### 6.1 Maturidade (NIST CSF 2.0 + CMMI)
Cada **Subcategoria** recebe um **Nível Atual (1–5)** e um **Nível Alvo (1–5)** dentro de um `AssessmentScope` (Processo × Área), com comentários e evidências:

| Nível | Nome | Score |
|---|---|---|
| 1 | Performed (ad hoc) | 1 |
| 2 | Documented | 2 |
| 3 | Managed | 3 |
| 4 | Quantitatively Managed | 4 |
| 5 | Optimizing | 5 |

**Agregação** (idêntica aos seus *Pivots*, com pesos opcionais, default uniforme):
```
score(Categoria) = média(score Subcategorias)
score(Função)    = média(score Categorias)
score(Geral)     = média(score Funções)   // ou média ponderada por criticidade do processo
gap = scoreAlvo − scoreAtual
```
O resultado alimenta o **radar por Função** e a leitura de **Tier** (Partial→Adaptive) do CSF.

### 6.2 Risco (matriz + tratamento + residual)
Derivado do seu *Sistema de Gestão de Riscos V2*. Um **Risk** liga ameaça × vulnerabilidade a um **Processo** numa **BU**, com dono e criticidade. A avaliação usa três fatores 1–4:

```
RiskLevel = Probabilidade + Impacto + ValorDoProcesso      // faixa 3–12
```
> Validado com seus dados: SEC0001 (1+3+1)=5 → MÉDIO; SEC0002 (4+4+3)=11 → CRÍTICO.
> A aba "Matriz de Risco" usa uma visualização 2D (Prob×Impacto, `2·Prob+Impacto`) para o heatmap. **A fórmula e as faixas são configuráveis por tenant** (`RiskAppetite.ThresholdsJson`) — recomendo confirmarmos os cortes de banda (sugestão default: 3–4 Baixo · 5–7 Médio · 8–9 Alto · 10–12 Crítico).

Ciclo: **Inerente → Tratamento (Aceitar/Mitigar/Transferir/Evitar) → Residual**. Planos de ação têm prazo e status; **planos vencidos** disparam escalonamento e entram no ICR.

### 6.3 Score ICR — Índice de Criticidade de Risco Cibernético
A contribuição "contínua e executiva" (tese Perinity). Combina fatores ponderados (0–100):

| Fator | Peso | Origem do dado |
|---|---|---|
| Severidade Técnica | 20% | scanner/EDR (CVSS) via conector |
| Criticidade do Ativo | 20% | `Asset.Criticality` (CMDB/onboarding) |
| Impacto no Negócio | 20% | `BusinessProcess.ProcessValue` + BIA |
| Exploração Recente | 10% | threat intel / "exploited in the wild" |
| Exposição Regulatória | 5% | mapeamento LGPD/obrigações (GV.OC-03) |
| Efetividade dos Controles | 15% | `SubcategoryEvaluation` + sinais de conector |
| Plano de Ação Vencido | 10% | `ActionPlan.Status == Vencido` |

```
ICR = Σ (fatorNormalizado_i × peso_i) × 100      // 0–39 Controlado · 40–59 Moderado · 60–79 Alto · 80–100 Crítico
```
Os pesos vivem em configuração (`IcrWeightProfile`), versionável por tenant.

---

## 7. Motor de evidências (dual-source)

```
        DOCUMENTAL (passivo)                         FATOS / API (ativo)
   políticas, procedimentos, planos            Secure Score, Defender Exposure,
   (upload / SharePoint / link)                Purview Compliance, Azure Advisor,
            │                                  Config Analyzer, SIEM, EDR, CMDB
            ▼                                              │
   IAaiService.AnalyzeDocument()              IEvidenceConnector.CollectAsync()
   → extrai afirmações, mapeia a               → EvidenceSignal[] normalizados
     subcategorias, gera resumo                  (SignalKey, valor, severidade)
            │                                              │
            └───────────────► EVIDENCE + SIGNALS ◄─────────┘
                                     │
                       IAaiService.SuggestMaturity(subcat, answers, evidence, signals)
                                     │   → {currentLevel, confidence, rationale, evidenceRefs}
                                     ▼
                       SubcategoryEvaluation (revisão humana) → Snapshots → Dashboards
```

A **convergência** entre autodeclaração, documento e fato de API é o que diferencia Aegis Score de um questionário comum: a IA aponta divergências (ex.: BU declara "MFA habilitado" mas Secure Score Identity = 31%) e ajusta a confiança/recomendação.

---

## 8. Conectores & onboarding

Tudo que é específico do cliente é resolvido no **onboarding**, sem código:

1. **Tenant & BUs** — cliente, áreas, gestores.
2. **Processos & ativos** — processos críticos, valor (1–4), ativos e criticidade (importável de CMDB).
3. **Conectores** — escolha das ferramentas (Microsoft 365/Defender/Purview/Azure, Google Workspace/SCC, AWS Security Hub, Sentinel, Splunk, CrowdStrike, etc.), credenciais (armazenadas criptografadas), intervalo de sync.
4. **Perfil-alvo** — Target Profile NIST (ou um Community Profile setorial) como meta.
5. **Apetite de risco & pesos ICR** — faixas de risco e pesos do índice.

**Contrato do conector** (núcleo da extensibilidade):
```csharp
public interface IEvidenceConnector
{
    string Provider { get; }            // "Microsoft"
    ConnectorCapability Capability { get; } // SecureScore, DefenderExposure, ...
    Task<ConnectorHealth> TestAsync(ConnectorConfig cfg, CancellationToken ct);
    IAsyncEnumerable<EvidenceSignal> CollectAsync(ConnectorConfig cfg, CancellationToken ct);
}
```
Cada conector traduz a API nativa em `EvidenceSignal` + um `SignalMapping` que liga o sinal às subcategorias NIST. Prints da Microsoft viram, por exemplo:

| Print (fonte) | Capability | Sinais → Subcategorias NIST |
|---|---|---|
| Secure Score 53,77% (Identity/Data/Device/Apps) | SecureScore | PR.AA, PR.DS, PR.PS |
| Defender Exposure 68 / vuln mgmt | DefenderExposure | ID.RA, ID.AM, PR.PS, DE.CM |
| Purview Compliance 69% (LGPD/normas) | PurviewCompliance | GV.OC-03, GV.PO, PR.DS |
| Azure Advisor — Segurança 37% | AzureAdvisor | PR.PS, PR.IR, PR.AA |
| Config Analyzer (Antispam/Antiphishing/DKIM) | ConfigAnalyzer | PR.AA, PR.PS, DE.CM |

### 8.1 Camada de coleta — adapters open-source (a "língua franca")

Para **não depender de agentes proprietários** (EDR específico) nem reimplementar coletores frágeis, os adapters reaproveitam ferramentas open-source consolidadas como *motor de coleta*; o Aegis Score apenas **normaliza** a saída para `EvidenceSignal`:

- **Endpoint / Sistema Operacional → Osquery.** Expõe o SO como tabelas SQL (firewall ativo? RDP aberto? BitLocker habilitado? nível de patch? processos/serviços?), de forma agnóstica de fornecedor — sem precisar de um agente EDR específico só para ler configuração. Alimenta `PR.PS`, `PR.IR`, `DE.CM`, `ID.AM`.
- **Nuvem (CSPM) → Steampipe / CloudQuery.** As APIs de Azure/GCP/AWS são caóticas e mudam o tempo todo; essas ferramentas extraem a postura por uma camada SQL/ETL única e absorvem essa volatilidade. Alimentam `PR.AA`, `PR.DS`, `PR.PS`, `ID.AM`, `DE.CM`.
- **SIEM / EDR → coletor de API.** Lê alertas/detecções (Sentinel, Splunk, Defender, SentinelOne, CrowdStrike) e os reduz a sinais comparáveis (volume, severidade, cobertura MITRE ATT&CK, tempo de resposta) — **sem depender das regras nativas** de cada produto. Alimentam `DE.CM`, `DE.AE`, `RS.*`.

Cada adapter devolve `EvidenceSignal[]`. Quando a saída é **desconhecida ou não-estruturada** (export arbitrário, log cru de uma ferramenta nova), ela passa antes pelo **normalizador por IA** (§9): a aplicação ingere produtos heterogêneos sem precisar de um parser dedicado por ferramenta.

---

## 9. Motor de IA

`IAiAssessmentService` (implementação default sobre a API da Anthropic/Claude, trocável):

- **AnalyzeDocumentAsync** — lê política/procedimento, extrai afirmações verificáveis, mapeia a subcategorias, resume e cita trechos.
- **SuggestMaturityAsync** — dada subcategoria + respostas + evidências + sinais, propõe `CurrentLevel (1–5)` com `confidence`, `rationale` e `evidenceRefs`.
- **ConductInterviewTurnAsync** — conduz entrevista/questionário conversacional (follow-up dirigido para preencher lacunas de evidência).
- **GenerateActionPlanAsync** — gera ações priorizadas por gap × risco × ICR, com "o quê/quem/como/quando".
- **GenerateExecutiveReportAsync** — compõe o Plano Diretor (maturidade por processo, riscos, fragilidades, oportunidades) em linguagem de negócio.
- **NormalizeSignalsAsync** — *parser dinâmico*. Recebe a saída **bruta e não-estruturada** de uma ferramenta desconhecida (logs, JSON arbitrário, CSV de export), identifica os campos essenciais (Host, IP, Severidade, Ação, Recurso…) e os estrutura no `EvidenceSignal` padronizado do Core, mapeando a subcategorias quando possível. É o que permite ingerir dezenas de produtos sem um parser codificado para cada um — IA aplicada de forma estratégica sobre o volume de logs heterogêneos.

Salvaguardas: toda saída de IA é **sugestão revisável**, com confiança e rastreabilidade; nenhuma decisão de risco é automatizada sem aprovação; prompts e respostas ficam na trilha de auditoria.

---

## 10. Arquitetura técnica

**Stack:** Back-end **C# .NET (ASP.NET Core, API REST)** + **Entity Framework Core**; banco **PostgreSQL**; front-end **React**. Clean Architecture, multi-tenant.

```
AegisScore.sln
└─ src/
   ├─ AegisScore.Domain            // entidades, enums, regras puras (sem dependências)
   ├─ AegisScore.Application        // serviços de aplicação, interfaces (IAiAssessmentService,
   │                           //   IEvidenceConnector, IConnectorRegistry), scoring
   ├─ AegisScore.Infrastructure     // EF Core (AegisScoreDbContext), seeder, AI (Claude), cripto, tenant ctx
   ├─ AegisScore.Connectors.Microsoft  // plugin de conectores Microsoft (Secure Score, Defender...)
   └─ AegisScore.Api                // ASP.NET Core: Program.cs, controllers, DTOs, auth, DI
data/
   └─ nist_csf_2_0_catalog.json   // seed do framework
frontend/
   └─ src/  // React: ExecutiveDashboard + componentes (radar, heatmap, cards, gaps)
```

**Camadas:** API → Application → Domain; Infrastructure implementa as interfaces da Application (DIP). Conectores são plugins descobertos por `IConnectorRegistry`. PostgreSQL com `jsonb` para campos flexíveis (referências, sinais, fatores). Migrations via EF Core. Background worker (`IHostedService`) faz a coleta periódica dos conectores.

---

## 11. API REST (principais endpoints)

Todos sob `/api/v1`, escopados por tenant (header `X-Tenant` ou claim no JWT).

```
GET    /framework/active                      catálogo NIST ativo (funções→categorias→subcategorias)
GET    /framework/maturity-levels             escala 1–5

POST   /tenants                               cria cliente (onboarding)
POST   /tenants/{id}/business-units           cadastra BU
POST   /tenants/{id}/processes                cadastra processo (valor 1–4)
POST   /tenants/{id}/connectors               cadastra conector (credenciais cifradas)
POST   /tenants/{id}/connectors/{cid}/test    testa conexão
POST   /tenants/{id}/connectors/{cid}/sync    força coleta → EvidenceSignal[]

POST   /assessments                           cria campanha
POST   /assessments/{id}/scopes               adiciona escopo (Processo × BU)
GET    /assessments/{id}/scopes/{sid}/questionnaire   questionário do escopo
POST   /assessments/{id}/scopes/{sid}/answers          submete respostas
POST   /assessments/{id}/scopes/{sid}/evidence         anexa evidência (doc/link)
POST   /assessments/{id}/scopes/{sid}/ai-suggest       IA sugere maturidade do escopo
PUT    /assessments/{id}/scopes/{sid}/evaluations/{code} valida nível atual/alvo (humano)
GET    /assessments/{id}/maturity             snapshots (overall/função/categoria/gap)

GET    /risks            POST /risks          registro de riscos
POST   /risks/{id}/evaluations                avaliação (inerente/residual)
POST   /risks/{id}/action-plans               plano de ação
GET    /icr?subjectType=Process               scores ICR

GET    /dashboard/executive                   payload do dashboard executivo
POST   /reports/master-plan                   gera Plano Diretor (IA) → PDF/DOCX
```

---

## 12. Front-end & dashboards (a exemplo dos prints)

Visão executiva (tema dark on-brand com o Synapse), composta por:
- **Cards de exposição** — Processos Críticos Expostos, Controles Ineficazes, Planos de Ação Vencidos, ICR médio.
- **Radar de maturidade por Função** (GV/ID/PR/DE/RS/RC) — Atual × Alvo.
- **Gap por Função/Categoria** — barras Atual vs Alvo.
- **Heatmap de risco** (Prob × Impacto) com concentração de impacto.
- **Efetividade de controles** — % de controles testados/efetivos.
- **Status de planos de ação** — no prazo / vencidos.
- **Compliance & obrigações** (LGPD) e **gauge do ICR** (0–100).

O scaffold inclui `ExecutiveDashboard.tsx` com radar (recharts), heatmap, cards e gap-chart consumindo `GET /dashboard/executive`.

---

## 13. Multi-tenant, segurança, auditoria, LGPD

- **Isolamento** por `TenantId` em todas as entidades operacionais (global query filter no EF).
- **Segredos** de conector criptografados (AES via Data Protection / Key Vault) — chaves nunca em claro no banco.
- **RBAC**: papéis (Admin, Analista, Auditor, Leitor/Executivo) — Auditor tem leitura + trilha; Executivo só dashboards/relatórios.
- **Trilha de auditoria imutável** (append-only) para evidências, sugestões de IA, validações e mudanças de risco — sustenta a 3ª linha (Auditoria) e a rastreabilidade que a tese GRC exige.
- **LGPD**: minimização (coletamos *postura*, não dados pessoais do cliente final), retenção configurável, e o próprio Aegis Score monitora GV.OC-03 (obrigações legais).
- **Ação destrutiva e credencial**: nenhuma credencial é inserida pela IA; integrações usam OAuth/Client Credentials concedidos pelo cliente no onboarding.

---

## 14. Integração com o Synapse

Aegis Score é um módulo do portal Synapse OS:
- **SSO** com o Synapse (mesmo IdP); papéis herdados.
- **Fila de Casos / SIEM/EDR** do Synapse alimentam `EvidenceSignal` (vulnerabilidades, incidentes) — reuso dos conectores que o Synapse já mantém.
- **Relatórios** do Synapse ganham o template "Plano Diretor / Maturidade".
- **Dashboard Executivo** do Synapse evolui de telemetria de SOC (casos, MTTR) para **exposição de negócio** (maturidade, risco, ICR) — exatamente o salto "modelo fraco → modelo forte".

---

## 15. Roadmap por fases

**Fase 0 — Fundação (este scaffold).** Domínio + EF/PostgreSQL + seed NIST + serviços de pontuação + abstrações de conector e IA + API mínima + dashboard React.

**Fase 1 — MVP de maturidade.** Onboarding (tenant/BU/processo), questionário digital + evidência documental, sugestão de maturidade por IA com revisão humana, snapshots e dashboard executivo. Relatório Plano Diretor (IA → DOCX/PDF).

**Fase 2 — Coleta ativa.** Conectores Microsoft (Secure Score, Defender Exposure, Purview, Azure Advisor, Config Analyzer) + worker de sync + SignalMapping → maturidade confrontada com fatos.

**Fase 3 — Risco & ICR contínuos.** Risk Engine completo (inerente/residual/apetite), ICR com pesos configuráveis, escalonamento de planos vencidos, alertas.

**Fase 4 — Multi-stack & escala.** Conectores Google/AWS/Splunk/CrowdStrike, Community Profiles setoriais, benchmarking entre clientes (MSSP), automações de re-coleta e re-pontuação contínuas.

---

## 16. O que vem no scaffold

- `ARCHITECTURE.md` (este documento) e `README.md` (como rodar).
- `data/nist_csf_2_0_catalog.json` — catálogo NIST CSF 2.0 completo (6/22/106 + 5 níveis).
- **Backend .NET**: `AegisScore.Domain` (modelo completo), `AegisScore.Application` (interfaces + serviços de pontuação Maturidade/Risco/ICR), `AegisScore.Infrastructure` (`AegisScoreDbContext`, seeder, serviço de IA Claude), `AegisScore.Connectors.Microsoft` (conector Secure Score de exemplo), `AegisScore.Api` (Program + controllers principais + DTOs).
- **Frontend React**: `ExecutiveDashboard.tsx` + componentes + cliente de API tipado.

> Status: **fundação arquitetural + scaffold**. Não é o produto final compilado ponta-a-ponta; é a base correta e extensível para a Fase 1. Os pontos a confirmar com vocês estão sinalizados (faixas de risco, pesos do ICR, prioridade de conectores, e qual LLM usar na implementação do `IAiAssessmentService`).
