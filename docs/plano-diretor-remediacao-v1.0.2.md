# AEGIS — Plano Diretor de Remediação v1.0.2

**Classificação:** Documento de governança técnica e segurança<br>
**Data de atualização:** 2026-07-21<br>
**Situação do programa:** Em execução<br>
**Branch de referência:** `main`<br>
**Commit de referência após o PR 0:** `c3a0bd3`<br>
**Em PR:** Reconciliação documental — PR #2, branch `docs/reconcile-operational-state`<br>
**Próximo pacote:** `AEGIS-TECH-001 — Alinhamento do backend com .NET 10 e EF Core 10` — **PRÓXIMO / implementação não autorizada**<br>
**Pacote seguinte:** `AEGIS-AUD-053 — Persistir e proteger o Data Protection Key Ring` — **PLANEJADO / aguarda o AEGIS-TECH-001**

> Este documento é a fonte de governança do programa de remediação. O código local e `docs/pr0-baseline.md` são a fonte de verdade para o estado técnico executável.

---

## 1. Histórico de revisões

| Versão | Data | Alteração |
|---|---|---|
| 1.0 | 2026-07-20 | Consolidação inicial da auditoria, épicos, gates e backlog mestre. |
| 1.0.1 | 2026-07-20 | Registra a conclusão do PR 0, PR #1, commit de merge `c3a0bd3`, baseline em `docs/pr0-baseline.md` e define `AEGIS-AUD-053` como próximo pacote. |
| 1.0.2 | 2026-07-21 | Registra a reconciliação documental como PR #2, formaliza o AEGIS-TECH-001 como pacote técnico anterior ao AEGIS-AUD-053 e alinha a sequência de remediação. |

## 2. Estado executivo atual

| Item | Estado | Evidência |
|---|---|---|
| PR 0 — Linha de base técnica | **CONCLUÍDO** | PR #1; merge `c3a0bd3`; `docs/pr0-baseline.md` |
| Backend build | **APROVADO COM WARNING CONHECIDO** | 0 erros; 1 warning `CS8604` |
| Testes backend | **APROVADO** | 219/219, 0 falhas, 0 ignorados |
| Frontend build | **APROVADO COM WARNINGS CONHECIDOS** | exit 0; 4 warnings de budget CSS |
| Frontend tests/lint | **NÃO IMPLEMENTADOS** | Pendência `AEGIS-AUD-033` |
| CI/CD | **NÃO IMPLEMENTADO** | Pendência `AEGIS-AUD-056` |
| Produção MSSP | **BLOQUEADA** | Gate G7 não aprovado |
| Reconciliação documental | **EM PR** | PR #2; branch `docs/reconcile-operational-state` |
| Próximo pacote | **PRÓXIMO / AGUARDA APROVAÇÃO DE IMPLEMENTAÇÃO** | `AEGIS-TECH-001` |
| Pacote seguinte | **PLANEJADO / AGUARDA AEGIS-TECH-001** | `AEGIS-AUD-053` |

### 2.1 O que o PR 0 concluiu

- Estabeleceu uma baseline reproduzível do repositório.
- Registrou stack, versões, comandos, limitações e warnings preexistentes.
- Confirmou a árvore limpa e a sincronização da `main` no momento do merge.
- Não corrigiu nenhum achado `AEGIS-AUD-*`.

### 2.2 O que permanece inalterado

- **Os 63 achados `AEGIS-AUD-*` continuam todos abertos.** Nenhum foi encerrado nesta revisão.
- O `AEGIS-AUD-053` permanece aberto e passa de **PRÓXIMO** para **PLANEJADO / aguarda o AEGIS-TECH-001** — é mudança de ordem de execução, não de estado de remediação.
- **O `AEGIS-TECH-001` é um pacote técnico de precedência, não um novo achado da auditoria.** Ele não entra no backlog mestre e **não altera a contagem de 63 achados**.
- A liberação para produção continua bloqueada.
- O Plano Diretor não substitui a inspeção do código local antes de cada mudança.

---

## 3. Objetivo do programa

Remediar as lacunas de arquitetura, segurança, integridade, operação e produto sem reescrita ampla, preservando interfaces públicas e isolamento multi-tenant.

Resultados esperados:

1. NIST CSF 2.0 permanece como núcleo neutro de tecnologia.
2. Regras determinísticas são a autoridade da avaliação.
3. Toda nota é reproduzível e rastreável até evidências e versões.
4. Dashboard, histórico e relatório compartilham a mesma projeção publicada.
5. Identidade global e memberships por tenant mantêm isolamento forte.
6. Conectores são adaptadores substituíveis.
7. A plataforma opera com filas duráveis, observabilidade, CI/CD e continuidade.

## 4. Princípios de execução

- Uma pendência ou conjunto pequeno e coeso por PR.
- Investigação sem alterações antes de cada implementação.
- Código local é a fonte de verdade durante o trabalho do Claude.
- GitHub é a fonte de publicação e revisão formal.
- Nenhum commit direto em `main`.
- Mudanças pequenas, reversíveis e testáveis.
- Não misturar correção com refatoração estética.
- Não enfraquecer multi-tenancy, autorização ou criptografia.
- Não considerar a tarefa concluída apenas porque compila.
- Atualizar este documento após cada merge.

## 5. Fluxo operacional de cada pacote

```text
Selecionar item → investigar localmente → revisar plano → implementar em branch →
executar testes → revisar diff → commit/push autorizado → PR → revisão formal →
merge → sincronizar main → atualizar Plano Diretor
```

Estados permitidos:

`ABERTA` → `EM INVESTIGAÇÃO` → `PLANEJADA` → `EM IMPLEMENTAÇÃO` → `EM REVISÃO` → `EM VALIDAÇÃO` → `CONCLUÍDA`

Estados auxiliares: `BLOQUEADA`, `ADIADA`, `DESCARTADA`.

## 6. Gates e marcos

| Gate | Condição | Marco |
|---|---|---|
| G0 | Baseline técnica versionada e reproduzível | **APROVADO** pelo PR #1 |
| G1 | Contenção imediata e bloqueadores de fundação corrigidos | M1 — Base segura para evolução |
| G2 | Identidade e isolamento multi-tenant validados | M2 — Tenant-safe |
| G3 | Motor determinístico e evidências auditáveis | M3 — Avaliação confiável |
| G4 | Projeção executiva, snapshot e relatório consistentes | M4 — Postura publicável |
| G5A/G5B | Conectores neutros e experiência NIST completa | M5 — Produto funcional |
| G6 | Operação, observabilidade e homologação | M6 — Pronto para homologação |
| G7 | Hardening, supply chain, privacidade e continuidade | M7 — Pronto para produção MSSP |

---

## 7. Sequência de remediação

### 7.1 Sequência oficial aprovada

| # | Pacote | Estado |
|---:|---|---|
| 1 | Reconciliação documental — PR #2 | **EM PR** |
| 2 | `AEGIS-TECH-001 — Alinhamento do backend com .NET 10 e EF Core 10` | **PRÓXIMO / AGUARDA APROVAÇÃO DE IMPLEMENTAÇÃO** |
| 3 | Atualização da baseline e dos documentos operacionais | **PLANEJADO** |
| 4 | `AEGIS-AUD-053 — Persistência e proteção do Data Protection Key Ring` | **PLANEJADO / AGUARDA AEGIS-TECH-001** |

> **A aprovação desta ordem não autoriza a implementação de nenhum pacote.** Ela define apenas a
> sequência de execução. **Cada pacote exige aprovação explícita própria** para sair de `PLANEJADO`
> e entrar em implementação. Em particular, **a implementação do `AEGIS-TECH-001` não está
> autorizada** por este documento.

### 7.2 Próximo pacote imediato

```text
Identificador: AEGIS-TECH-001
Título: Alinhamento do backend com .NET 10 e EF Core 10
Branch planejada: chore/tech-001-net10-efcore10
Estado: PRÓXIMO / implementação não autorizada
```

Motivo da precedência sobre o `AEGIS-AUD-053`: os projetos já direcionam `net10.0`, mas a camada de
persistência permanece na linha EF Core `8.0.x` / Npgsql `8.0.x`. O `AEGIS-AUD-053` introduz
persistência de key ring — e, portanto, uma migration nova. Executá-lo sobre uma base desalinhada
tornaria impossível distinguir mudança de schema deliberada de efeito colateral de um upgrade
posterior de ORM.

**Objetivo:**

- alinhar EF Core, provider PostgreSQL/Npgsql e pacotes Microsoft vinculados à plataforma;
- preservar o modelo de dados;
- não gerar migration funcional;
- validar migrations, snapshot, isolamento de tenant e grafo de dependências;
- preparar uma base coerente para o `AEGIS-AUD-053`.

**Exclusões explícitas do escopo:**

- persistência do Data Protection Key Ring;
- tabela `DataProtectionKeys`;
- certificados;
- alteração funcional de schema;
- refatoração ampla do `DbContext`;
- reescrita de migrations históricas.

**Gates obrigatórios:**

- restore sem conflito ou downgrade;
- build sem warning novo;
- testes da baseline;
- ausência de mudança pendente no modelo;
- migrations históricas e snapshot sem alteração inesperada;
- validação em PostgreSQL descartável;
- banco vazio e banco criado pela versão anterior;
- isolamento de tenant;
- auditoria de vulnerabilidades;
- rollback por reversão do pacote, sem alteração de banco.

**Natureza do pacote:** o `AEGIS-TECH-001` é um **pacote técnico de precedência**, não um achado da
auditoria. Não recebe identificador `AEGIS-AUD-*`, não entra no backlog mestre e **não altera a
contagem de 63 achados**.

### 7.3 Pacote seguinte

**`AEGIS-AUD-053 — Persistir e proteger o Data Protection Key Ring`** — **PLANEJADO / aguarda a
conclusão do `AEGIS-TECH-001`.**

Motivo da prioridade dentro do EP-00:

- é bloqueador direto de produção;
- possui escopo relativamente isolado;
- protege a capacidade de descriptografar configurações de conectores após restart e scale-out;
- permite validar o processo de investigação, implementação, teste e revisão antes das mudanças maiores.

Branch sugerida:

`fix/aud-053-data-protection-keyring`

### Ordem macro

## EP-00 — Linha de base e contenção imediata

**Estado:** EM EXECUÇÃO<br>
**Objetivo:** Preservar uma referência reproduzível e remover riscos imediatos antes de mudanças estruturais.<br>
**Dependências:** Nenhuma além da baseline técnica concluída.<br>
**Ordem interna:** PR 0 concluído. Reconciliação documental em PR (#2). Próximo pacote obrigatório: **AEGIS-TECH-001** (pacote técnico de precedência, fora do backlog de achados), seguido da atualização da baseline e só então **AEGIS-AUD-053**.

### Pacotes do épico

| Ordem | ID | Severidade | Pendência | Área | Estado |
|---:|---|---|---|---|---|
| 1 | AEGIS-AUD-046 | ALTO | Eliminar dados reais ou identificáveis dos stubs e demos | Privacy / Demo Data / Repository Hygiene | ABERTA |
| 2 | AEGIS-AUD-057 | MÉDIO | Remover credenciais padrão do arquivo principal de configuração | Configuration Security | ABERTA |
| 3 | AEGIS-AUD-053 | BLOQUEADOR | Persistir e proteger o Data Protection Key Ring | Cryptography / Connector Secrets | PLANEJADO / AGUARDA AEGIS-TECH-001 |
| 4 | AEGIS-AUD-052 | ALTO | Retirar migrations e seed da inicialização concorrente da API | Deployment / Database | ABERTA |
| 5 | AEGIS-AUD-050 | BLOQUEADOR | Não usar filas em memória como mecanismo operacional durável | Workers / Reliability / Scale-out | ABERTA |
| 6 | AEGIS-AUD-026 | ALTO | Não substituir falha da API por dados de demonstração em ambiente operacional | Frontend / Data Integrity | ABERTA |
| 7 | AEGIS-AUD-031 | MÉDIO | Alinhar documentação arquitetural com a stack e o estado reais | Documentation / Architecture Governance | ABERTA |

### Gate de aceite

G1 — Nenhum bloqueador criptográfico ou de durabilidade permanece sem plano aprovado; dados de demonstração e credenciais triviais não podem alcançar produção.

### Testes mínimos do épico

Build e testes da baseline; testes de persistência/isolamento do key ring; restart; múltiplas réplicas; fila durável; smoke tests documentais.

### Estratégia de rollback

Cada alteração deve ser aditiva ou configurável. Chaves e ciphertext antigos não podem ser descartados. Mudanças de deployment devem preservar caminho de reversão.

### Definition of Done do épico

- Todos os itens do épico foram implementados ou formalmente adiados com risco aceito.
- Testes positivos, negativos, regressão e segurança foram executados.
- Nenhum warning novo foi incorporado sem justificativa.
- Documentação, runbooks e este Plano Diretor foram atualizados.
- PRs foram revisados e mergeados; ambiente aplicável foi validado.
- O gate correspondente foi aprovado explicitamente.

---

## EP-01 — Identidade, autorização e isolamento multi-tenant

**Estado:** PLANEJADO<br>
**Objetivo:** Garantir que uma identidade corporativa possa operar múltiplos tenants sem enfraquecer o isolamento de dados e permissões.<br>
**Dependências:** EP-00 aprovado. Baseline e gestão de segredos estáveis.<br>
**Ordem interna:** Executar após EP-00. Começar por invariantes de persistência e refresh tokens antes de federação.

### Pacotes do épico

| Ordem | ID | Severidade | Pendência | Área | Estado |
|---:|---|---|---|---|---|
| 1 | AEGIS-AUD-007 | ALTO | Integrar autenticação corporativa federada | Identity / Authentication | ABERTA |
| 2 | AEGIS-AUD-008 | ALTO | Proteger alterações e exclusões cross-tenant no DbContext | Multi-tenancy / Persistence | ABERTA |
| 3 | AEGIS-AUD-009 | ALTO | Armazenar somente hash de refresh tokens | Authentication / Session Security | ABERTA |
| 4 | AEGIS-AUD-010 | ALTO | Separar provisionamento global de concessão de acesso a tenant | Identity Governance | ABERTA |
| 5 | AEGIS-AUD-011 | MÉDIO | Separar papéis globais de papéis por tenant | Authorization | ABERTA |
| 6 | AEGIS-AUD-012 | MÉDIO | Exigir seleção explícita ou último tenant validado no login | UX / Authorization Context | ABERTA |
| 7 | AEGIS-AUD-013 | MÉDIO | Aplicar filtros de tenant por convenção e testar o modelo EF | Multi-tenancy / Architecture | ABERTA |
| 8 | AEGIS-AUD-014 | MÉDIO | Restringir e inventariar IgnoreQueryFilters() | Multi-tenancy / Privileged Access | ABERTA |
| 9 | AEGIS-AUD-015 | MÉDIO | Garantir consistência de tenant nos relacionamentos | Persistence / Data Integrity | ABERTA |
| 10 | AEGIS-AUD-016 | MÉDIO | Formalizar o SLA de revogação de access tokens | Authentication | ABERTA |
| 11 | AEGIS-AUD-017 | MÉDIO | Validar configuração de proxy para rate limiting | API Security | ABERTA |
| 12 | AEGIS-AUD-018 | BAIXO | Rejeitar X-Tenant inválido | API / Tenant Consistency | ABERTA |
| 13 | AEGIS-AUD-030 | ALTO | Invalidar e recarregar dados no tenant switch | Frontend / Multi-tenancy / Client State | ABERTA |

### Gate de aceite

G2 — Testes cross-tenant negativos aprovados; memberships, papéis, tokens e tenant switch demonstram isolamento.

### Testes mínimos do épico

Alteração/remoção cross-tenant; FK inconsistente; IgnoreQueryFilters; refresh token; tenant switch; requests atrasadas; revogação.

### Estratégia de rollback

Migrations compatíveis, dual-read quando necessário e preservação de claims/contratos públicos durante a transição.

### Definition of Done do épico

- Todos os itens do épico foram implementados ou formalmente adiados com risco aceito.
- Testes positivos, negativos, regressão e segurança foram executados.
- Nenhum warning novo foi incorporado sem justificativa.
- Documentação, runbooks e este Plano Diretor foram atualizados.
- PRs foram revisados e mergeados; ambiente aplicável foi validado.
- O gate correspondente foi aprovado explicitamente.

---

## EP-02 — Motor determinístico de avaliação e evidências

**Estado:** PLANEJADO<br>
**Objetivo:** Tornar o resultado oficial reproduzível, tipado e independente do LLM e de fornecedores.<br>
**Dependências:** EP-00 aprovado. Requer decisões sobre fórmula e estados de avaliação.<br>
**Ordem interna:** Começar por autoridade determinística, persistência de evidência e mapping único; depois consolidar fórmulas e semântica.

### Pacotes do épico

| Ordem | ID | Severidade | Pendência | Área | Estado |
|---:|---|---|---|---|---|
| 1 | AEGIS-AUD-001 | BLOQUEADOR | Formalizar a fórmula oficial de pontuação | Domínio / Scoring | ABERTA |
| 2 | AEGIS-AUD-002 | ALTO | Diferenciar “não avaliado” de nota zero | Scoring | ABERTA |
| 3 | AEGIS-AUD-003 | ALTO | Eliminar dependência de texto livre nas regras de avaliação | Assessment / Rules | ABERTA |
| 4 | AEGIS-AUD-004 | ALTO | Definir a relação entre avaliação por campanha e postura contínua | Domínio / Assessment | ABERTA |
| 5 | AEGIS-AUD-005 | MÉDIO | Tornar pendências e checklists entidades operacionais quando necessário | Domínio / Operação SOC | ABERTA |
| 6 | AEGIS-AUD-006 | MÉDIO | Evitar fornecedor principal derivado da ordem textual | Assessment / Neutralidade | ABERTA |
| 7 | AEGIS-AUD-019 | BLOQUEADOR | Remover a IA da autoridade primária sobre o veredito de conformidade | IA / Assessment / Scoring | ABERTA |
| 8 | AEGIS-AUD-020 | ALTO | Persistir evidência bruta normalizada na esteira principal de telemetria | Evidence / Telemetry / Auditability | ABERTA |
| 9 | AEGIS-AUD-025 | MÉDIO | Tratar confiança da IA como metadado, não validação | AI Assurance | ABERTA |
| 10 | AEGIS-AUD-043 | ALTO | Definir uma única autoridade de mapeamento sinal → NIST | Connectors / Framework Mapping | ABERTA |
| 11 | AEGIS-AUD-044 | MÉDIO | Remover semântica Microsoft das chaves do esquema normalizado | Unified Schema / Vendor Neutrality | ABERTA |
| 12 | AEGIS-AUD-045 | ALTO | Não usar o LLM como normalizador confiável de ferramentas desconhecidas | AI / Connectors / Supply-chain Data | ABERTA |

### Gate de aceite

G3 — Mesmas evidências + mesma versão de regra produzem o mesmo resultado; indisponibilidade do LLM não impede cálculo oficial.

### Testes mínimos do épico

Reprodutibilidade; evidência ausente/obsoleta; não avaliado; pesos; mapping; prompt injection; saída malformada do LLM.

### Estratégia de rollback

Manter a esteira anterior em modo sombra durante a migração, sem permitir que ela continue como autoridade.

### Definition of Done do épico

- Todos os itens do épico foram implementados ou formalmente adiados com risco aceito.
- Testes positivos, negativos, regressão e segurança foram executados.
- Nenhum warning novo foi incorporado sem justificativa.
- Documentação, runbooks e este Plano Diretor foram atualizados.
- PRs foram revisados e mergeados; ambiente aplicável foi validado.
- O gate correspondente foi aprovado explicitamente.

---

## EP-03 — Projeção executiva, snapshots e relatórios

**Estado:** PLANEJADO<br>
**Objetivo:** Criar uma única postura publicável e auditável para dashboard, histórico e relatório.<br>
**Dependências:** EP-02 aprovado.<br>
**Ordem interna:** Unificar projeção antes de construir relatórios; snapshot completo precede publicação e histórico.

### Pacotes do épico

| Ordem | ID | Severidade | Pendência | Área | Estado |
|---:|---|---|---|---|---|
| 1 | AEGIS-AUD-021 | BLOQUEADOR | Unificar a projeção executiva de postura | Dashboard / Scoring / Architecture | ABERTA |
| 2 | AEGIS-AUD-022 | ALTO | Retirar regras de negócio e acesso direto ao banco do DashboardController | API / Application Architecture | ABERTA |
| 3 | AEGIS-AUD-024 | MÉDIO | Não ocultar corrupção de dados explicativos do dashboard | Observability / Data Integrity | ABERTA |
| 4 | AEGIS-AUD-032 | ALTO | Formalizar o contrato entre maturidade executiva e tendência de postura | Frontend / Metrics Semantics | ABERTA |
| 5 | AEGIS-AUD-034 | ALTO | Implementar o módulo de relatórios como capacidade real do produto | Reports / Product Completeness | ABERTA |
| 6 | AEGIS-AUD-035 | BLOQUEADOR | Criar snapshot auditável da postura, não apenas totais agregados | Historical Posture / Auditability | ABERTA |
| 7 | AEGIS-AUD-036 | ALTO | Tornar snapshots publicados imutáveis | Historical Integrity | ABERTA |
| 8 | AEGIS-AUD-037 | ALTO | Registrar cobertura e denominador semântico da tendência | Scoring Trend / Metrics | ABERTA |
| 9 | AEGIS-AUD-038 | MÉDIO | Tornar a captura histórica resiliente a falhas e lacunas | Workers / Reliability | ABERTA |
| 10 | AEGIS-AUD-039 | MÉDIO | Definir timezone e corte temporal por tenant | Temporal Semantics / MSSP | ABERTA |
| 11 | AEGIS-AUD-040 | BLOQUEADOR | Gerar dashboard e relatório a partir da mesma projeção publicada | Reports / Executive Posture | ABERTA |

### Gate de aceite

G4 — Um snapshot ID produz os mesmos números em API, dashboard e relatório; snapshot publicado é imutável e explicável.

### Testes mínimos do épico

Reconstrução histórica; cobertura; revisão de snapshot; timezone; backfill; corrupção de JSON; equivalência dashboard/relatório.

### Estratégia de rollback

Preservar snapshots publicados e introduzir revisões, nunca sobrescrita silenciosa.

### Definition of Done do épico

- Todos os itens do épico foram implementados ou formalmente adiados com risco aceito.
- Testes positivos, negativos, regressão e segurança foram executados.
- Nenhum warning novo foi incorporado sem justificativa.
- Documentação, runbooks e este Plano Diretor foram atualizados.
- PRs foram revisados e mergeados; ambiente aplicável foi validado.
- O gate correspondente foi aprovado explicitamente.

---

## EP-04 — Neutralidade e extensibilidade de conectores

**Estado:** PLANEJADO<br>
**Objetivo:** Permitir novos fornecedores sem alterar o núcleo NIST ou criar pipelines específicos.<br>
**Dependências:** EP-02 aprovado; parte de EP-03 pode evoluir em paralelo.<br>
**Ordem interna:** Executar depois que a esteira determinística e a persistência de evidências estiverem definidas.

### Pacotes do épico

| Ordem | ID | Severidade | Pendência | Área | Estado |
|---:|---|---|---|---|---|
| 1 | AEGIS-AUD-023 | MÉDIO | Remover linguagem e conceitos Microsoft Secure Score do contrato central | Vendor Neutrality / Ubiquitous Language | ABERTA |
| 2 | AEGIS-AUD-041 | ALTO | Implementar um executor genérico para IEvidenceConnector | Connectors / Telemetry Ingestion | ABERTA |
| 3 | AEGIS-AUD-042 | MÉDIO | Não exigir alteração de enums centrais para cada novo fornecedor ou capacidade | Extensibility / Domain Contracts | ABERTA |
| 4 | AEGIS-AUD-047 | MÉDIO | Evoluir EncryptedSettings para configuração tipada e versionada | Connector Configuration / Operations | ABERTA |

### Gate de aceite

G5A — Um conector de prova não Microsoft é registrado, coleta e alimenta a mesma esteira sem mudanças no scoring.

### Testes mínimos do épico

Registry; capability; schema; config versionada; retries; idempotência; provider ausente; secret handling.

### Estratégia de rollback

Adapters são removíveis sem alterar contratos do núcleo; feature flags para novos providers.

### Definition of Done do épico

- Todos os itens do épico foram implementados ou formalmente adiados com risco aceito.
- Testes positivos, negativos, regressão e segurança foram executados.
- Nenhum warning novo foi incorporado sem justificativa.
- Documentação, runbooks e este Plano Diretor foram atualizados.
- PRs foram revisados e mergeados; ambiente aplicável foi validado.
- O gate correspondente foi aprovado explicitamente.

---

## EP-05 — Experiência de produto e frontend NIST

**Estado:** PLANEJADO<br>
**Objetivo:** Representar de forma simétrica as seis Funções NIST e impedir mistura ou retenção de dados entre tenants.<br>
**Dependências:** EP-01 e EP-03 aprovados.<br>
**Ordem interna:** Executar após contratos de projeção e scoring estabilizados.

### Pacotes do épico

| Ordem | ID | Severidade | Pendência | Área | Estado |
|---:|---|---|---|---|---|
| 1 | AEGIS-AUD-027 | ALTO | Implementar páginas equivalentes para as seis Funções NIST | Frontend / Product Completeness | ABERTA |
| 2 | AEGIS-AUD-028 | MÉDIO | Separar postura NIST de painéis orientados a produto ou domínio técnico | Frontend / Information Architecture | ABERTA |
| 3 | AEGIS-AUD-029 | MÉDIO | Remover fornecedor específico do título e contrato do painel de identidade | Frontend / Vendor Neutrality | ABERTA |
| 4 | AEGIS-AUD-033 | MÉDIO | Adicionar suíte de testes automatizados do frontend | Frontend / Quality | ABERTA |

### Gate de aceite

G5B — Seis páginas equivalentes, testes de tenant switch e nenhuma métrica fictícia ou ambígua.

### Testes mínimos do épico

Rotas; DTOs; tenant switch; requests atrasadas; estados vazios; falha de API; E2E de login e postura.

### Estratégia de rollback

Rotas antigas podem coexistir temporariamente, sem duplicar regra de negócio no cliente.

### Definition of Done do épico

- Todos os itens do épico foram implementados ou formalmente adiados com risco aceito.
- Testes positivos, negativos, regressão e segurança foram executados.
- Nenhum warning novo foi incorporado sem justificativa.
- Documentação, runbooks e este Plano Diretor foram atualizados.
- PRs foram revisados e mergeados; ambiente aplicável foi validado.
- O gate correspondente foi aprovado explicitamente.

---

## EP-06 — Observabilidade, resiliência e engenharia operacional

**Estado:** PLANEJADO<br>
**Objetivo:** Tornar o serviço operável, diagnosticável e escalável como plataforma MSSP.<br>
**Dependências:** EP-00 aprovado; contratos principais estabilizados.<br>
**Ordem interna:** Pode iniciar parcialmente após EP-00, mas os gates finais dependem dos fluxos definitivos.

### Pacotes do épico

| Ordem | ID | Severidade | Pendência | Área | Estado |
|---:|---|---|---|---|---|
| 1 | AEGIS-AUD-048 | ALTO | Implementar health checks de liveness e readiness | Operations / Availability | ABERTA |
| 2 | AEGIS-AUD-049 | ALTO | Adicionar métricas, tracing distribuído e correlação fim a fim | Observability | ABERTA |
| 3 | AEGIS-AUD-051 | ALTO | Separar workers da API ou coordená-los para múltiplas réplicas | Deployment Architecture | ABERTA |
| 4 | AEGIS-AUD-054 | MÉDIO | Não persistir mensagem bruta de exceção como erro de documento | Error Handling / Sensitive Data | ABERTA |
| 5 | AEGIS-AUD-055 | ALTO | Formalizar SLOs, alertas e métricas operacionais por tenant | MSSP Operations | ABERTA |
| 6 | AEGIS-AUD-056 | ALTO | Implementar CI/CD e controles de supply chain | CI/CD / Supply Chain | ABERTA |
| 7 | AEGIS-AUD-058 | MÉDIO | Tornar CORS configurável e validado por ambiente | API Security / Deployment | ABERTA |

### Gate de aceite

G6 — Homologação com health/readiness, tracing, métricas por tenant, SLOs, workers coordenados e checks obrigatórios.

### Testes mínimos do épico

Scale-out; retry; shutdown; métricas; tracing; falha de dependência; CORS; pipeline; logs sanitizados.

### Estratégia de rollback

Instrumentação deve ser desabilitável; workers devem ter deploy e rollback independentes.

### Definition of Done do épico

- Todos os itens do épico foram implementados ou formalmente adiados com risco aceito.
- Testes positivos, negativos, regressão e segurança foram executados.
- Nenhum warning novo foi incorporado sem justificativa.
- Documentação, runbooks e este Plano Diretor foram atualizados.
- PRs foram revisados e mergeados; ambiente aplicável foi validado.
- O gate correspondente foi aprovado explicitamente.

---

## EP-07 — Hardening, privacidade, supply chain e continuidade

**Estado:** PLANEJADO<br>
**Objetivo:** Atender ao gate de produção MSSP com controles de segurança operacional, privacidade e recuperação.<br>
**Dependências:** G1 a G6 aprovados.<br>
**Ordem interna:** Concluir após os épicos técnicos; partes de supply chain e segredo podem começar antes.

### Pacotes do épico

| Ordem | ID | Severidade | Pendência | Área | Estado |
|---:|---|---|---|---|---|
| 1 | AEGIS-AUD-059 | ALTO | Implementar hardening de produção | Production Hardening | ABERTA |
| 2 | AEGIS-AUD-060 | ALTO | Formalizar estratégia de gestão de segredos | Secrets Management | ABERTA |
| 3 | AEGIS-AUD-061 | ALTO | Implementar programa de Supply Chain | Software Supply Chain | ABERTA |
| 4 | AEGIS-AUD-062 | MÉDIO | Formalizar requisitos de LGPD e retenção | Privacy / Governance | ABERTA |
| 5 | AEGIS-AUD-063 | ALTO | Definir estratégia de continuidade | Business Continuity / Disaster Recovery | ABERTA |

### Gate de aceite

G7 — Production Readiness aprovado; RPO/RTO, restore, hardening, LGPD e supply chain validados.

### Testes mínimos do épico

DR; restore; rotação de segredo; SAST/SCA/SBOM; assinatura; headers/TLS; retenção e descarte.

### Estratégia de rollback

Planos de recuperação testados; mudanças de hardening com validação em staging e possibilidade de reversão controlada.

### Definition of Done do épico

- Todos os itens do épico foram implementados ou formalmente adiados com risco aceito.
- Testes positivos, negativos, regressão e segurança foram executados.
- Nenhum warning novo foi incorporado sem justificativa.
- Documentação, runbooks e este Plano Diretor foram atualizados.
- PRs foram revisados e mergeados; ambiente aplicável foi validado.
- O gate correspondente foi aprovado explicitamente.

---

## 16. Investigações residuais

| ID | Estado | Tema | Próxima ação |
|---|---|---|---|
| AEGIS-INV-001 | CONVERTIDA | Modelo do dashboard | Convertida em AEGIS-AUD-021. |
| AEGIS-INV-002 | CONVERTIDA | Modelo do relatório | Convertida em AEGIS-AUD-034 e AEGIS-AUD-040. |
| AEGIS-INV-003 | CONVERTIDA | Quem grava TenantControlState.CurrentScore | Convertida em AEGIS-AUD-019. |
| AEGIS-INV-004 | ABERTA | Prompts e saídas da IA | Validar redaction, schema, retenção, logs, isolamento e custo. |
| AEGIS-INV-005 | CONVERTIDA | Frontend das seis Funções | Convertida em AEGIS-AUD-027 e AEGIS-AUD-028. |
| AEGIS-INV-006 | PARCIAL | Google/AWS e novos conectores | Continuar durante EP-04. |
| AEGIS-INV-007 | ABERTA | Migrations da refatoração de identidade | Executar antes das mudanças do EP-01. |
| AEGIS-INV-008 | PARCIAL | Frontend de autenticação e tenant switch | Risco residual registrado em AEGIS-AUD-030. |
| AEGIS-INV-009 | CONVERTIDA | Workers e contexto de tenant | Convertida em AEGIS-AUD-050 e AEGIS-AUD-051. |
| AEGIS-INV-010 | CONVERTIDA | CI/CD, segredos e produção | Convertida em AEGIS-AUD-056 e itens do EP-07. |
| AEGIS-INV-011 | ABERTA | Isolamento de MaturitySnapshot | Inventariar leituras, escritas, FKs e autorização. |
| AEGIS-INV-012 | ABERTA | Ciclo operacional do MicrosoftSecureScoreConnector | Confirmar executor e persistência real. |
| AEGIS-INV-013 | ABERTA | Uso efetivo de SignalMapping | Confirmar autoridade e consumidores. |

## 17. Backlog mestre

| ID | Severidade | Área | Pendência | Épico | Estado | PR | Commit |
|---|---|---|---|---|---|---|---|
| AEGIS-AUD-001 | BLOQUEADOR | Domínio / Scoring | Formalizar a fórmula oficial de pontuação | EP-02 | ABERTA | — | — |
| AEGIS-AUD-002 | ALTO | Scoring | Diferenciar “não avaliado” de nota zero | EP-02 | ABERTA | — | — |
| AEGIS-AUD-003 | ALTO | Assessment / Rules | Eliminar dependência de texto livre nas regras de avaliação | EP-02 | ABERTA | — | — |
| AEGIS-AUD-004 | ALTO | Domínio / Assessment | Definir a relação entre avaliação por campanha e postura contínua | EP-02 | ABERTA | — | — |
| AEGIS-AUD-005 | MÉDIO | Domínio / Operação SOC | Tornar pendências e checklists entidades operacionais quando necessário | EP-02 | ABERTA | — | — |
| AEGIS-AUD-006 | MÉDIO | Assessment / Neutralidade | Evitar fornecedor principal derivado da ordem textual | EP-02 | ABERTA | — | — |
| AEGIS-AUD-007 | ALTO | Identity / Authentication | Integrar autenticação corporativa federada | EP-01 | ABERTA | — | — |
| AEGIS-AUD-008 | ALTO | Multi-tenancy / Persistence | Proteger alterações e exclusões cross-tenant no DbContext | EP-01 | ABERTA | — | — |
| AEGIS-AUD-009 | ALTO | Authentication / Session Security | Armazenar somente hash de refresh tokens | EP-01 | ABERTA | — | — |
| AEGIS-AUD-010 | ALTO | Identity Governance | Separar provisionamento global de concessão de acesso a tenant | EP-01 | ABERTA | — | — |
| AEGIS-AUD-011 | MÉDIO | Authorization | Separar papéis globais de papéis por tenant | EP-01 | ABERTA | — | — |
| AEGIS-AUD-012 | MÉDIO | UX / Authorization Context | Exigir seleção explícita ou último tenant validado no login | EP-01 | ABERTA | — | — |
| AEGIS-AUD-013 | MÉDIO | Multi-tenancy / Architecture | Aplicar filtros de tenant por convenção e testar o modelo EF | EP-01 | ABERTA | — | — |
| AEGIS-AUD-014 | MÉDIO | Multi-tenancy / Privileged Access | Restringir e inventariar IgnoreQueryFilters() | EP-01 | ABERTA | — | — |
| AEGIS-AUD-015 | MÉDIO | Persistence / Data Integrity | Garantir consistência de tenant nos relacionamentos | EP-01 | ABERTA | — | — |
| AEGIS-AUD-016 | MÉDIO | Authentication | Formalizar o SLA de revogação de access tokens | EP-01 | ABERTA | — | — |
| AEGIS-AUD-017 | MÉDIO | API Security | Validar configuração de proxy para rate limiting | EP-01 | ABERTA | — | — |
| AEGIS-AUD-018 | BAIXO | API / Tenant Consistency | Rejeitar X-Tenant inválido | EP-01 | ABERTA | — | — |
| AEGIS-AUD-019 | BLOQUEADOR | IA / Assessment / Scoring | Remover a IA da autoridade primária sobre o veredito de conformidade | EP-02 | ABERTA | — | — |
| AEGIS-AUD-020 | ALTO | Evidence / Telemetry / Auditability | Persistir evidência bruta normalizada na esteira principal de telemetria | EP-02 | ABERTA | — | — |
| AEGIS-AUD-021 | BLOQUEADOR | Dashboard / Scoring / Architecture | Unificar a projeção executiva de postura | EP-03 | ABERTA | — | — |
| AEGIS-AUD-022 | ALTO | API / Application Architecture | Retirar regras de negócio e acesso direto ao banco do DashboardController | EP-03 | ABERTA | — | — |
| AEGIS-AUD-023 | MÉDIO | Vendor Neutrality / Ubiquitous Language | Remover linguagem e conceitos Microsoft Secure Score do contrato central | EP-04 | ABERTA | — | — |
| AEGIS-AUD-024 | MÉDIO | Observability / Data Integrity | Não ocultar corrupção de dados explicativos do dashboard | EP-03 | ABERTA | — | — |
| AEGIS-AUD-025 | MÉDIO | AI Assurance | Tratar confiança da IA como metadado, não validação | EP-02 | ABERTA | — | — |
| AEGIS-AUD-026 | ALTO | Frontend / Data Integrity | Não substituir falha da API por dados de demonstração em ambiente operacional | EP-00 | ABERTA | — | — |
| AEGIS-AUD-027 | ALTO | Frontend / Product Completeness | Implementar páginas equivalentes para as seis Funções NIST | EP-05 | ABERTA | — | — |
| AEGIS-AUD-028 | MÉDIO | Frontend / Information Architecture | Separar postura NIST de painéis orientados a produto ou domínio técnico | EP-05 | ABERTA | — | — |
| AEGIS-AUD-029 | MÉDIO | Frontend / Vendor Neutrality | Remover fornecedor específico do título e contrato do painel de identidade | EP-05 | ABERTA | — | — |
| AEGIS-AUD-030 | ALTO | Frontend / Multi-tenancy / Client State | Invalidar e recarregar dados no tenant switch | EP-01 | ABERTA | — | — |
| AEGIS-AUD-031 | MÉDIO | Documentation / Architecture Governance | Alinhar documentação arquitetural com a stack e o estado reais | EP-00 | ABERTA | — | — |
| AEGIS-AUD-032 | ALTO | Frontend / Metrics Semantics | Formalizar o contrato entre maturidade executiva e tendência de postura | EP-03 | ABERTA | — | — |
| AEGIS-AUD-033 | MÉDIO | Frontend / Quality | Adicionar suíte de testes automatizados do frontend | EP-05 | ABERTA | — | — |
| AEGIS-AUD-034 | ALTO | Reports / Product Completeness | Implementar o módulo de relatórios como capacidade real do produto | EP-03 | ABERTA | — | — |
| AEGIS-AUD-035 | BLOQUEADOR | Historical Posture / Auditability | Criar snapshot auditável da postura, não apenas totais agregados | EP-03 | ABERTA | — | — |
| AEGIS-AUD-036 | ALTO | Historical Integrity | Tornar snapshots publicados imutáveis | EP-03 | ABERTA | — | — |
| AEGIS-AUD-037 | ALTO | Scoring Trend / Metrics | Registrar cobertura e denominador semântico da tendência | EP-03 | ABERTA | — | — |
| AEGIS-AUD-038 | MÉDIO | Workers / Reliability | Tornar a captura histórica resiliente a falhas e lacunas | EP-03 | ABERTA | — | — |
| AEGIS-AUD-039 | MÉDIO | Temporal Semantics / MSSP | Definir timezone e corte temporal por tenant | EP-03 | ABERTA | — | — |
| AEGIS-AUD-040 | BLOQUEADOR | Reports / Executive Posture | Gerar dashboard e relatório a partir da mesma projeção publicada | EP-03 | ABERTA | — | — |
| AEGIS-AUD-041 | ALTO | Connectors / Telemetry Ingestion | Implementar um executor genérico para IEvidenceConnector | EP-04 | ABERTA | — | — |
| AEGIS-AUD-042 | MÉDIO | Extensibility / Domain Contracts | Não exigir alteração de enums centrais para cada novo fornecedor ou capacidade | EP-04 | ABERTA | — | — |
| AEGIS-AUD-043 | ALTO | Connectors / Framework Mapping | Definir uma única autoridade de mapeamento sinal → NIST | EP-02 | ABERTA | — | — |
| AEGIS-AUD-044 | MÉDIO | Unified Schema / Vendor Neutrality | Remover semântica Microsoft das chaves do esquema normalizado | EP-02 | ABERTA | — | — |
| AEGIS-AUD-045 | ALTO | AI / Connectors / Supply-chain Data | Não usar o LLM como normalizador confiável de ferramentas desconhecidas | EP-02 | ABERTA | — | — |
| AEGIS-AUD-046 | ALTO | Privacy / Demo Data / Repository Hygiene | Eliminar dados reais ou identificáveis dos stubs e demos | EP-00 | ABERTA | — | — |
| AEGIS-AUD-047 | MÉDIO | Connector Configuration / Operations | Evoluir EncryptedSettings para configuração tipada e versionada | EP-04 | ABERTA | — | — |
| AEGIS-AUD-048 | ALTO | Operations / Availability | Implementar health checks de liveness e readiness | EP-06 | ABERTA | — | — |
| AEGIS-AUD-049 | ALTO | Observability | Adicionar métricas, tracing distribuído e correlação fim a fim | EP-06 | ABERTA | — | — |
| AEGIS-AUD-050 | BLOQUEADOR | Workers / Reliability / Scale-out | Não usar filas em memória como mecanismo operacional durável | EP-00 | ABERTA | — | — |
| AEGIS-AUD-051 | ALTO | Deployment Architecture | Separar workers da API ou coordená-los para múltiplas réplicas | EP-06 | ABERTA | — | — |
| AEGIS-AUD-052 | ALTO | Deployment / Database | Retirar migrations e seed da inicialização concorrente da API | EP-00 | ABERTA | — | — |
| AEGIS-AUD-053 | BLOQUEADOR | Cryptography / Connector Secrets | Persistir e proteger o Data Protection Key Ring | EP-00 | PLANEJADO / AGUARDA AEGIS-TECH-001 | — | — |
| AEGIS-AUD-054 | MÉDIO | Error Handling / Sensitive Data | Não persistir mensagem bruta de exceção como erro de documento | EP-06 | ABERTA | — | — |
| AEGIS-AUD-055 | ALTO | MSSP Operations | Formalizar SLOs, alertas e métricas operacionais por tenant | EP-06 | ABERTA | — | — |
| AEGIS-AUD-056 | ALTO | CI/CD / Supply Chain | Implementar CI/CD e controles de supply chain | EP-06 | ABERTA | — | — |
| AEGIS-AUD-057 | MÉDIO | Configuration Security | Remover credenciais padrão do arquivo principal de configuração | EP-00 | ABERTA | — | — |
| AEGIS-AUD-058 | MÉDIO | API Security / Deployment | Tornar CORS configurável e validado por ambiente | EP-06 | ABERTA | — | — |
| AEGIS-AUD-059 | ALTO | Production Hardening | Implementar hardening de produção | EP-07 | ABERTA | — | — |
| AEGIS-AUD-060 | ALTO | Secrets Management | Formalizar estratégia de gestão de segredos | EP-07 | ABERTA | — | — |
| AEGIS-AUD-061 | ALTO | Software Supply Chain | Implementar programa de Supply Chain | EP-07 | ABERTA | — | — |
| AEGIS-AUD-062 | MÉDIO | Privacy / Governance | Formalizar requisitos de LGPD e retenção | EP-07 | ABERTA | — | — |
| AEGIS-AUD-063 | ALTO | Business Continuity / Disaster Recovery | Definir estratégia de continuidade | EP-07 | ABERTA | — | — |

## 18. Registro de execução

| Entrega | Estado | Branch | PR | Merge/Commit | Testes | Observações |
|---|---|---|---|---|---|---|
| PR 0 — Baseline técnica | CONCLUÍDO | `chore/pr0-baseline` (removida) | #1 | `c3a0bd3` | Backend 219/219; frontend build aprovado | `docs/pr0-baseline.md` |
| PR #2 — Reconciliação documental | **EM PR** | `docs/reconcile-operational-state` | #2 | — (não mergeado) | Não executados — escopo documental | Commits `7ea19dc`, `27ee185` e o commit desta revisão do Plano Diretor |
| AEGIS-TECH-001 — .NET 10 / EF Core 10 | **PRÓXIMO / AGUARDA APROVAÇÃO** | `chore/tech-001-net10-efcore10` (não criada) | — | — | A definir após aprovação | Pacote técnico de precedência, fora do backlog de achados; implementação não autorizada |
| AEGIS-AUD-053 — Data Protection Key Ring | **PLANEJADO / AGUARDA AEGIS-TECH-001** | `fix/aud-053-data-protection-keyring` | — | — | A definir após investigação | Não iniciar implementação sem revisão do plano nem antes da conclusão do TECH-001 |

### Campos obrigatórios após cada merge

```text
ID:
Status:
PR:
Commit de merge:
Data:
Responsável:
Critérios validados:
Testes executados:
Risco residual:
Pendências desbloqueadas:
Rollback validado:
```

## 19. Definition of Done global

Uma pendência só pode ser marcada como `CONCLUÍDA` quando:

1. O problema foi revalidado no código atual.
2. O escopo e os contratos foram aprovados.
3. A implementação possui testes adequados.
4. Build e testes da baseline continuam aprovados.
5. Não há nova regressão de tenant, segurança ou compatibilidade.
6. O diff foi revisado.
7. O PR foi mergeado.
8. A validação no ambiente aplicável foi concluída.
9. O rollback foi documentado e, quando relevante, testado.
10. Este Plano Diretor foi atualizado.

## 20. Critérios de parada

Interromper implementação ou implantação quando houver:

- possível perda de dados ou de key ring;
- risco de exposição cross-tenant;
- migration irreversível sem backup;
- divergência crítica entre código local e `main`;
- segredo real em código, log ou fixture;
- falha de teste não explicada;
- alteração fora do escopo;
- ausência de rollback para mudança crítica;
- incompatibilidade com ciphertext, snapshot ou contrato publicado.

## 21. Decisão de produção

**Arquitetura:** aprovada com ressalvas.<br>
**Continuidade do desenvolvimento:** recomendada.<br>
**Produção MSSP:** não recomendada enquanto G7 não estiver aprovado.

Não é recomendada uma reescrita ampla. A remediação deve permanecer incremental, orientada por evidência e gates.

## 22. Uso deste documento com agentes de programação

Ao iniciar uma nova sessão do Claude:

1. Usar esta versão `v1.0.2`, versionada em `docs/plano-diretor-remediacao-v1.0.2.md` — não anexar
   cópias externas nem versões anteriores.
2. Informar que o código local é a fonte de verdade.
3. Pedir leitura de `docs/pr0-baseline.md` e de `docs/handoff-operacional.md`.
4. Selecionar apenas um pacote ou conjunto coeso, respeitando a sequência oficial da seção 7.1.
5. Iniciar com investigação sem alterações.
6. Revisar o plano antes de autorizar implementação. **A ordem aprovada na seção 7.1 não é
   autorização de implementação:** cada pacote exige aprovação explícita própria.
7. Não permitir commit/push sem autorização explícita.

---

**Fim do Plano Diretor de Remediação v1.0.2**
