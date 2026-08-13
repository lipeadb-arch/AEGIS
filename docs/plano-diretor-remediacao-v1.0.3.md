# AEGIS — Plano Diretor de Remediação v1.0.3

**Classificação:** instrumento privado de priorização técnica e continuidade<br>
**Data de atualização:** 2026-08-11<br>
**Horizonte desta revisão:** entrega funcional em 30 dias, até 2026-08-28<br>
**Branch de referência:** `main`<br>
**Commit de referência:** `dab5e6e`<br>
**Última entrega concluída:** Snapshots auditáveis de postura (`AEGIS-AUD-035/036/037`) — PR #26, squash-merge `dab5e6e` (árvore idêntica ao head validado `47a5cd5`; inclui a correção da corrida no `ControlStateWriter`; AEGIS KNIGHT/Google Workspace em `24e8e61`/PR #25; MVP acelerado Entregas 0–5 já concluído)<br>
**Próximo trabalho:** MVP acelerado **CONCLUÍDO** (Entregas 0–5) e **fundação do AEGIS KNIGHT CONCLUÍDA** — PR #23 (squash-merge `d53beb3`; árvore idêntica ao head validado `1b59989`; branch `feat/aegis-knight-foundation` removida): assessment de identidade/exposição **SOMENTE demonstração**, catálogo original `ak-knight-v1` (`AK-ENTRA-001..005`), fórmula própria `knight-score-v1` **separada** do AEGIS Score, vereditos determinísticos, IA apenas consultiva com fallback, FK composta tenant/run + unicidade por indicador, migration `20260803140802_Knight_Foundation` (25ª); backend **559/559**, `dotnet build` **0/0**, `ng build` aprovado, **smoke funcional e visual** em PostgreSQL descartável (`aegis_dev` intocado; score 23 / cobertura 100% / 5 indicadores / `advisoryFromAi=false` / isolamento). **Microsoft Graph real, AD local, Okta, PDF e integrações externas ainda NÃO existem.** **Sequência acelerada do AEGIS KNIGHT** (sem criar AUDs individuais por indicador): **Entrega 2** — Microsoft Entra ID **real** + ~10–15 indicadores de alto valor; **Entrega 3** — relatório, histórico comparativo e exportação; **Entrega 4** — correlação de evidências SIEM/EDR; **Entrega 5** — AD local/híbrido, se o prazo permitir. **Entregas 1 e 2 do AEGIS KNIGHT CONCLUÍDAS. A Entrega 2 (coleta REAL do Microsoft Entra ID + arquitetura MULTICOLETOR) foi CONCLUÍDA — PR #24 squash-merge `ea38bd6` (árvore idêntica ao head validado `bc4f69c`; branch `feat/aegis-knight-multicollector-entra-live` removida local e remota): coletor Graph somente-leitura por client credentials, 15 indicadores `AK-ENTRA-001..015`, integração configurável pela interface (IdentityPosture, provider Microsoft=0 / capability=10, "Abrir AEGIS KNIGHT" em vez dos botões genéricos), avaliação estritamente FAIL-CLOSED (base de login/Graph constantes oficiais + `@odata.nextLink` validado — o bearer não vaza; paginação não truncada em silêncio; MFA vazio/malformado e coberturas incompletas de MFA/atividade de privilegiados → NotEvaluated; AK-ENTRA-010 mede permissões CONCEDIDAS e AK-ENTRA-013 consentimento DELEGADO tenant-wide, não `requiredResourceAccess`; Conditional Access só aprova cobertura global comprovada; estados reais de falha preservados; token malformado → AuthenticationFailure sanitizada), Demo preservado (score 23); migration `20260803190253_Knight_MultiCollector` (26ª), sem migration nova nas correções; `has-pending-model-changes` limpo; backend 585/585, `dotnet build` 0/0, `ng build` aprovado. Microsoft Graph validado por HTTP SIMULADO — AINDA NÃO contra um tenant Entra real; sem credenciais/segredos/dados reais. Coletor REAL Google Workspace somente leitura CONCLUÍDO — PR #25 (squash-merge `24e8e61`; árvore idêntica ao head `0d0f18d`; branch removida): catálogo `ak-knight-v2`, 6 indicadores `AK-GWS-001..006`, service account com domain-wide delegation (biblioteca oficial `Google.Apis.Auth`), escopos somente leitura Directory/Reports, host oficial fixo, paginação fail-closed, só metadados administrativos/auditoria; sem migration; testes focados KNIGHT+Google 51/51, `dotnet build` 0/0, `ng build` aprovado; Demo (score 23), Entra e AEGIS Score geral preservados; validado por HTTP SIMULADO, ainda não contra tenant Google real. Próximo trabalho técnico imediato: AEGIS KNIGHT — relatório executivo, histórico comparativo e exportação.** ⚠️ **Validação no notebook corporativo: PENDENTE / NÃO EXECUTADA** — sem evidência de portabilidade corporativa até o Felipe executar o roteiro nessa máquina

**Primeiro pacote pós-MVP CONCLUÍDO — snapshots auditáveis de postura (`AEGIS-AUD-035`, `AEGIS-AUD-036`, `AEGIS-AUD-037`): PR #26, squash-merge `dab5e6e`** (árvore idêntica ao head validado `47a5cd5`; branch `feat/aegis-auditable-history` removida local e remota). Fotografia IMUTÁVEL COMPARTILHADA entre AEGIS Score/NIST e AEGIS KNIGHT (sem score combinado), publicação controlada por papel **Manager/TenantAdmin**, universo NIST COMPLETO com "não avaliado" ≠ 0 e ≠ NonCompliant, proveniência da evidência DECISIVA, hash determinístico INEQUÍVOCO, imutabilidade reforçada por **gatilho append-only no PostgreSQL**, comparação só entre compatíveis (senão estado explícito de incompatibilidade) e tela compartilhada `/history` (histórico, detalhe e comparação). Migration ADITIVA `20260811143617_Auditable_Posture_Snapshots` (a **27ª**). O mesmo PR corrige a **corrida concorrente no upsert do ledger** (`ControlStateWriter`): recuperação pontual da inserção perdida, reaplicando o veredito como UPDATE sem duplicar (sem migration). Validação real: `dotnet build` **0/0**, backend **611/611**, **bateria PostgreSQL 20/20** (gatilho + corrida do ledger + `Concurrent_MesmoEvento` aprovado 3×), DbMigrator em banco descartável (`aegis_dev` intocado), `ng build` e **smoke visual** do histórico aprovados. **Exportação executiva PDF/CSV (`AEGIS-AUD-034`) CONCLUÍDA** — PR #27 (squash-merge `b9cbcba`; árvore idêntica ao head validado `957aecd`; branch `feat/aud-034-executive-export` removida): endpoint `GET /api/v1/posture/snapshots/{id}/export?format=pdf|csv` com PDF executivo pt-BR (PDFsharp-MigraDoc 6.2.0, edição CORE/MIT, sem GDI/Office) e CSV (UTF-8+BOM, `;`, protegido contra CSV/Formula Injection), ambos derivados EXCLUSIVAMENTE da `PostureSnapshot` imutável (tenant implícito, hash reverificado → 404/400/409); botões Baixar PDF/CSV no detalhe de `/history`; **sem migration**, sem mudança de fórmula/catálogo/ledger/Dashboard. Validação (árvore incorporada): `dotnet build` **0/0**, **backend 622/622** (+11 focados, incl. equivalência detalhe×PDF×CSV), `ng build` aprovado, `has-pending-model-changes` limpo (**27 migrations, nenhuma nova**; modelo EF sem mudança), smoke visual dos PDFs/CSVs. **`AEGIS-AUD-040` PARCIAL**: relatório da MESMA projeção publicada atendido; Dashboard ao vivo ligado a snapshot fica como resíduo (fora do escopo, **NÃO bloqueia a homologação**). **Risco operacional:** a geração de PDF exige fonte TTF instalada — validar no futuro container **Linux** (fonte instalada ou empacotada). **Próximo trabalho prioritário: implantação de homologação em nuvem e validação com tenant REAL (Microsoft Entra ID, depois ingestão genérica autenticada de SIEM/EDR); o resíduo do AUD-040 fica para depois, salvo bloqueador real.**

> Este plano não exige mais concluir os 63 achados antes de apresentar o produto. O objetivo imediato é
> entregar um **MVP funcional, demonstrável e pronto para homologação**, preservando segurança
> multi-tenant e integridade da pontuação. Os demais achados permanecem registrados para o pós-MVP.

---

## 1. Decisão executiva

O processo anterior tratava cada `AEGIS-AUD-*` como um projeto independente, com investigação extensa,
PR isolado e repetição de toda a bateria de testes. Esse modelo é seguro, mas incompatível com o prazo de
um mês.

A partir desta revisão:

- o trabalho será organizado por **fluxos verticais de produto**, não por um AUD por vez;
- até cinco PRs técnicos devem encerrar o MVP após o PR #16;
- um PR pode resolver vários achados relacionados;
- investigação deixa de ser uma fase separada: deve ser curta e ocorrer dentro da implementação;
- testes serão proporcionais ao risco e não repetidos em SHAs inalterados;
- documentação será limitada aos dois handoffs existentes, depois de cada merge;
- achados que não bloqueiam a demonstração, a segurança do tenant ou a integridade do score ficam
  formalmente **ADIADOS PARA PÓS-MVP**.

O alvo desta revisão não é uma produção MSSP completa com HA, DR, LGPD, supply chain e todos os
fornecedores certificados. O alvo é um produto coerente e funcional que possa ser instalado, conectado a
um tenant de homologação, receber evidências, calcular postura e apresentar os resultados.

---

## 2. O que significa “AEGIS funcional e apresentável”

Até 2026-08-28, deve ser possível executar um roteiro ponta a ponta:

1. Preparar o PostgreSQL pelo `AegisScore.DbMigrator` e iniciar API e frontend.
2. Criar ou usar um tenant AEGIS, autenticar uma identidade e selecionar o ambiente correto.
3. Configurar uma integração sem persistir segredo em claro.
4. Receber eventos de SIEM/EDR por uma entrada autenticada e neutra de fornecedor.
5. Persistir a evidência recebida, sua origem, horário, integridade e mapeamento NIST.
6. Atualizar os controles e o AEGIS Score por regras determinísticas.
7. Diferenciar claramente controle **não avaliado**, **conforme** e **não conforme**.
8. Exibir Dashboard executivo com score, cobertura, controles críticos, conectores e recência dos dados.
9. Navegar pelas seis Funções do NIST CSF 2.0:
   - Governar — GV;
   - Identificar — ID;
   - Proteger — PR;
   - Detectar — DE;
   - Responder — RS;
   - Recuperar — RC.
10. Em cada Função, apresentar conteúdo, controles, estado, evidências e checklist de pendências.
11. Usar o Document Hub para upload, processamento, listagem, cobertura e consulta de documentos.
12. Trocar de tenant sem manter dados, cache ou indicadores do tenant anterior.

### Critério de honestidade do produto

- Integração real deve ser identificada como real.
- Stub, payload sintético ou adaptador ainda não implementado deve ser identificado como demonstração.
- A tela de Integrações não pode declarar um fornecedor “operacional” se não existir adaptador funcional.
- Ausência de evidência não pode virar nota zero nem conformidade.
- Falha da API não pode ser substituída silenciosamente por dados de demonstração.

---

## 3. Estado técnico aproveitável

Não é necessário reconstruir o projeto. A fundação existente deve ser reutilizada.

| Capacidade | Estado atual |
|---|---|
| .NET 10 / EF Core 10 / PostgreSQL | Concluído |
| Migrations externas ao startup e readiness de schema | Concluído |
| Segredos de conectores cifrados e key ring persistente | Concluído |
| Filas de documento e sync duráveis | Concluído |
| Escritas cross-tenant protegidas | Concluído |
| Refresh tokens persistidos somente por hash | Concluído |
| Login local, federação Entra e tenant switch | Implementados; falta fechar a experiência |
| Identidade global e membership por tenant | Concluído |
| Papéis globais e tenant-scoped | Concluído (PR #16) |
| Catálogo NIST CSF 2.0 | Existente |
| Document Hub | Existente e integrado ao backend; requer aceite ponta a ponta |
| Páginas GV, ID, PR, DE, RS e RC | Estrutura existente; conteúdo/estado ainda desigual |
| Configuração de conectores | Existente com segredos cifrados |
| Coleta real de SIEM/EDR | Incompleta; adaptadores atuais não comprovam operação real |
| Evidência normalizada e score | Parcial; autoridade e rastreabilidade precisam ser fechadas |
| Dashboard executivo | Existente; projeções e semântica ainda precisam ser unificadas |
| Testes backend | 537/537 na main (`9db7ba4`) |
| Frontend | Build aprovado; suíte ampla fica fora do MVP |

### Achados já concluídos

| ID | PR | Merge |
|---|---:|---|
| AEGIS-AUD-053 | #5 | `49a6747` |
| AEGIS-AUD-052 | #6 | `0ebad27` |
| AEGIS-AUD-057 | #7 | `9904729` |
| AEGIS-AUD-046 | #8 | `f9a3ed7` |
| AEGIS-AUD-050 | #9 | `f170b0f` |
| AEGIS-AUD-026 | #10 | `383bf6b` |
| AEGIS-AUD-031 | #11 | `d02cfee` |
| AEGIS-AUD-008 | #12 | `2f8c968` |
| AEGIS-AUD-009 | #13 | `37d57ff` |
| AEGIS-AUD-007 | #14 | `ff5d119` |
| AEGIS-AUD-010 | #15 | `d947328` |
| AEGIS-AUD-011 | #16 | `00937e9` |
| AEGIS-AUD-012 | #17 | `2fbc0d9` (squash) |
| AEGIS-AUD-018 | #17 | `2fbc0d9` (squash) |
| AEGIS-AUD-030 | #17 | `2fbc0d9` (squash) |
| AEGIS-AUD-020 | #18 | `c24c547` (squash) |
| AEGIS-AUD-041 | #18 | `c24c547` (squash) |
| AEGIS-AUD-043 | #18 | `c24c547` (squash) |
| AEGIS-AUD-001 | #19 | `c8ecb3f` (squash) |
| AEGIS-AUD-002 | #19 | `c8ecb3f` (squash) |
| AEGIS-AUD-019 | #19 | `c8ecb3f` (squash) |
| AEGIS-AUD-021 | #20 | `9db7ba4` (squash) |
| AEGIS-AUD-027 | #20 | `9db7ba4` (squash) |
| AEGIS-AUD-032 | #20 | `9db7ba4` (squash) |
| AEGIS-AUD-048 | #21 | `5a2f7a8` (squash) |

As **Entregas 1, 2, 3, 4 e 5** estão **CONCLUÍDAS** — Entrega 1 (fluxo de tenant confiável: `AEGIS-AUD-012`, `AEGIS-AUD-018`, `AEGIS-AUD-030`) em PR #17 (squash-merge `2fbc0d9`), Entrega 2 (ingestão operacional de evidências: `AEGIS-AUD-020`, `AEGIS-AUD-041`, `AEGIS-AUD-043`) em PR #18 (squash-merge `c24c547`), Entrega 3 (score determinístico e explicável: `AEGIS-AUD-001`, `AEGIS-AUD-002`, `AEGIS-AUD-019`) em PR #19 (squash-merge `c8ecb3f`) Entrega 4 (Workspace NIST, Dashboard e Document Hub: `AEGIS-AUD-021`, `AEGIS-AUD-027`, `AEGIS-AUD-032`) em PR #20 (squash-merge `9db7ba4`) e Entrega 5 (release candidate demonstrável: `AEGIS-AUD-048`) em PR #21 (squash-merge `5a2f7a8`). O **caminho crítico do MVP acelerado está CONCLUÍDO**; o que segue é o backlog pós-MVP (seção 6), sem novo AUD autorizado.

---

## 4. Caminho crítico de 30 dias

### Visão geral

| Ordem | Entrega vertical | AUDs prioritários | Prazo-alvo | Resultado visível |
|---:|---|---|---|---|
| 0 | Fechar separação de papéis | AEGIS-AUD-011 | ✅ Concluída | PR #16 mergeado (`00937e9`); autoridade global e tenant separadas |
| 1 | Fluxo de tenant confiável | AEGIS-AUD-012, AEGIS-AUD-018, AEGIS-AUD-030 | ✅ Concluída | PR #17 mergeado (`2fbc0d9`); login/seleção/switch sem retenção cross-tenant |
| 2 | Ingestão operacional de evidências | AEGIS-AUD-020, AEGIS-AUD-041, AEGIS-AUD-043 | ✅ Concluída | PR #18 mergeado (`c24c547`); ingestão genérica autenticada SIEM/EDR, evidência persistida e mapeada para NIST |
| 3 | Score determinístico e explicável | AEGIS-AUD-001, AEGIS-AUD-002, AEGIS-AUD-019 | ✅ Concluída | PR #19 mergeado (`c8ecb3f`); score reproduzível `aegis-score-v1`, IA consultiva, cobertura ≠ score |
| 4 | Workspace NIST, Dashboard e Hub | AEGIS-AUD-021, AEGIS-AUD-027, AEGIS-AUD-032 | ✅ Concluída | PR #20 mergeado (`9db7ba4`); projeção única `/scoring/workspace`, seis Funções equivalentes, Dashboard AEGIS Score, saúde honesta dos conectores |
| 5 | Release candidate demonstrável | AEGIS-AUD-048 + correções bloqueadoras | ✅ Concluída | PR #21 mergeado (`5a2f7a8`); `/health/live`+`/health/ready`, senha demo só em runtime, smoke `scripts/smoke-mvp.ps1` (35/35), correção do `<base href>`, roteiro em `DEV.md` |

Os IDs agrupam riscos já catalogados, mas o aceite é pelo **fluxo funcionando**, não por quantidade de
AUDs encerrados.

### Entrega 0 — concluir o PR #16 — ✅ CONCLUÍDA

**Status:** CONCLUÍDA — PR #16 squash-merge `00937e9`; `main` local/remota sincronizadas; branch removida.<br>
**Escopo:** finalizar o AEGIS-AUD-011 já implementado.<br>
**Fora do escopo:** reabrir arquitetura de identidade ou executar AUD-012 no mesmo PR.

Aceite:

- `TenantRole` e `PlatformRole` separados;
- backfill de `PlatformAdmin` não reativa membership inativo ou tenant suspenso;
- migration, constraints e testes PostgreSQL aprovados;
- merge e handoff concluídos.

### Entrega 1 — fluxo de tenant confiável

**Status:** ✅ CONCLUÍDA — PR #17 (squash-merge `2fbc0d9`); `main` local/remota sincronizadas; branch `feat/mvp-tenant-flow` removida.<br>
**AUDs:** AEGIS-AUD-012, AEGIS-AUD-018 e AEGIS-AUD-030 em um único PR.

Implementar apenas o necessário para:

- selecionar explicitamente um tenant quando houver mais de um acesso;
- usar último tenant somente após revalidar membership e status;
- rejeitar `X-Tenant` ausente, inválido ou divergente quando a rota exigir tenant;
- no switch, limpar estado e requisições do tenant anterior antes de carregar o novo;
- recarregar Dashboard, Hub, páginas NIST, integrações e indicadores;
- impedir que resposta atrasada do tenant anterior repovoe a UI;
- manter o login local e federado existentes.

Não criar um novo sistema de sessão nem ampliar o SLA de revogação neste pacote.

**Aceite (evidência):** login/troca federada com desfecho explícito — recusa (0 acessos) · seleção automática (1) · seleção explícita ou último tenant **revalidado** (vários), via **ticket curto purpose-bound** em `POST /auth/select-tenant`; `X-Tenant` **fail-closed** (ausente/vazio/malformado → 400; divergente ou token sem tenant → 403; família `/auth` isenta); no switch, o **cancelamento das leituras do tenant anterior precede a troca**, que envia o **Bearer local + o `X-Tenant` atual** e recarrega o novo tenant **sem resposta atrasada**. Backend **462/462**; `ng build` aprovado (4 warnings CSS conhecidos); **sem migration, schema ou credenciais**. Login local/federado, refresh e `TenantRole`×`PlatformRole` preservados.

### Entrega 2 — ingestão operacional de evidências

**Status:** ✅ CONCLUÍDA — PR #18 (squash-merge `c24c547`); `main` local/remota sincronizadas; branch `feat/mvp-evidence-ingestion` removida.<br>
**AUDs:** AEGIS-AUD-020, AEGIS-AUD-041 e AEGIS-AUD-043 em um único PR.

Objetivo: tornar o AEGIS capaz de **receber dados reais sem depender de um adaptador específico**.

Escopo mínimo:

- contrato normalizado e versionado para eventos de SIEM e EDR;
- entrada autenticada por credencial própria do conector, isolada por tenant;
- idempotência por identificador ou hash do evento;
- persistência do payload bruto protegido, origem, tipo, instante de coleta e instante de recebimento;
- executor único para coleta/push e atualização de `ConnectorConfig.LastSyncAt/LastStatus`;
- uma autoridade central de mapeamento `sinal → subcategorias NIST`;
- rejeição explícita de sinal sem mapeamento, sem pedir ao LLM para inventar um;
- endpoint/teste de conexão e estado visível na tela de Integrações;
- payloads de referência para pelo menos um formato SIEM e um formato EDR;
- marcação clara dos fornecedores que ainda não possuem adaptador real.

O caminho genérico autenticado é o requisito do MVP. Adaptadores completos para Sentinel, Splunk,
CrowdStrike, Google SecOps e outros podem ser adicionados depois sem bloquear a entrega.

**Aceite (evidência):** endpoint **`POST /api/v1/ingestion/connectors/{connectorId}/events`** autenticado por **`X-Ingestion-Key`** (persistida só como **hash SHA-256**, comparação em tempo constante); **tenant derivado exclusivamente do `ConnectorConfig` autenticado** (contrato v1 sem `TenantId`/score/veredito do cliente); respostas **400/401/422/200** + **rate limiting**; **idempotência como invariante de banco** (índice único parcial; concorrência real em PostgreSQL — 8 requisições → 1 persistência); **RawPayload protegido** (Data Protection, purpose próprio); **`SignalMapping` como única autoridade** de mapeamento, **sem LLM** no caminho; **executor único push/pull** para Generic SIEM e Generic EDR, com **adaptadores de fabricantes honestamente marcados como não implementados**; amostras `samples/ingestion` só com dados reservados/sintéticos. Migration **`20260730184530_Aud020GenericEvidenceIngestion`** (**24 migrations**); **sete `SignalMappings`**. Backend **472/472**; `AegisScore.DbMigrator --verify-only`/`has-pending-model-changes` aprovados; **smoke real PostgreSQL** aprovado (SIEM, EDR, deduplicação, chave inválida, sinal desconhecido, proteção do payload, ausência de LLM). **Sem credenciais, segredos ou dados reais.** Adaptadores reais de fabricantes permanecem fora desta entrega.

### Entrega 3 — score determinístico e explicável

**Status:** ✅ CONCLUÍDA — PR #19 (squash-merge `c8ecb3f`); `main` local/remota sincronizadas; branch `feat/mvp-deterministic-score` removida.<br>
**AUDs:** AEGIS-AUD-001, AEGIS-AUD-002 e AEGIS-AUD-019 em um único PR.

Escopo mínimo:

- uma fórmula oficial, simples, versionada e usada pelo backend;
- estados distintos para `NotEvaluated`, `Compliant` e `NonCompliant`;
- denominador calculado apenas sobre o universo semântico definido pela fórmula;
- telemetria e regra determinística como autoridades do veredito;
- IA limitada a resumo, explicação e recomendação;
- todo score rastreável aos controles e evidências que o compõem;
- motivo legível quando um controle não pontua;
- checklist derivado de controles sem evidência ou não conformes, sem criar um subsistema de workflow.

Não implementar neste MVP campanhas complexas, confiança estatística da IA ou fórmulas alternativas.

**Aceite (evidência):** fórmula oficial **`aegis-score-v1`** como autoridade ÚNICA de pontos, arredondamento e estado; **score ANULÁVEL** (0/0 = `NotEvaluated`, nunca 0%) e **cobertura** (peso avaliado / peso elegível do catálogo ativo) como eixos distintos; **agregação compartilhada** (`AegisScoreAggregator`) entre **Score Atual e snapshot diário**, restrita ao **framework ATIVO** — estado de versão antiga não entra no score nem infla a cobertura (avaliado ≤ elegível). Veredito determinístico por **telemetria + `SignalMapping.ScoringHint`** (`DeterministicControlEvaluator`/`EvidenceSignalEvaluator`); **IA limitada a resumo/explicação/recomendação — não decide conformidade**. Projeção da evidência no ledger com **recompute GLOBAL "from-newest" entre conectores** (SIEM+EDR no mesmo controle; evento antigo não sobrescreve o mais novo; empate exato de instante → pior veredito conservador, depois chave/Id estáveis; isolamento por tenant, sem `IgnoreQueryFilters`) e **falha de projeção NÃO mascarada** (carimba `LastStatus=Failed` e propaga; o retry deduplicado reprojeta sem duplicar `EvidenceSignal`). O hint **`percent.higherIsBetter.v1` é BINÁRIO** (≥ 80% → Compliant, **< 80% → NonCompliant**; valor ausente/NaN/infinito/fora de 0–100 ou unidade incompatível → sem veredito) — **`MitigatedByThirdParty` reservado a controle compensatório real** (terceiro/MSSP ou isolamento OT no `DeterministicControlEvaluator`), com crédito de 50% preservado na fórmula. Dashboard catalog-first com `NotEvaluated`; frontend exibe "Não avaliado" e cobertura (percentual anulável). **Validação:** backend **528/528**; **`dotnet build` 0 erros / 0 warnings**; **`ng build` aprovado** com os **4 warnings de budget CSS conhecidos**; **`has-pending-model-changes` limpo — SEM migration nova** (a coluna `ScoringHint` já existia; a Entrega 3 apenas passou o **seed idempotente** a preenchê-la); `AegisScore.DbMigrator` (migrate+seed+verify e `--verify-only`) aprovado, **24 migrations**, **sete `SignalMappings` com ScoringHint conhecido**; **smoke real PostgreSQL** aprovado (ingestão SIEM/EDR, mapping+hint, ledger determinístico, score/cobertura, deduplicação, ordenação global entre conectores e retry/reprojeção; base descartável, `aegis_dev` preservado com os sete mappings). **Sem credenciais, segredos ou dados reais.**

### Entrega 4 — Workspace NIST, Dashboard e Document Hub

**Status:** ✅ CONCLUÍDA — PR #20 (squash-merge `9db7ba4`); `main` local/remota sincronizadas; branch `feat/mvp-nist-workspace` removida.<br>
**AUDs:** AEGIS-AUD-021, AEGIS-AUD-027 e AEGIS-AUD-032 em um único PR.

Escopo mínimo:

- uma projeção única para score atual, cobertura, contagem de controles e severidades;
- Dashboard executivo consumindo essa projeção;
- todas as seis Funções com padrão visual e funcional equivalente:
  - descrição e objetivo;
  - score/estado da Função;
  - cobertura de evidências;
  - lista de controles;
  - evidência mais recente;
  - pendências/checklist;
  - estados loading, vazio e erro;
- Govern mantém o Document Hub como área especializada;
- Identify mantém inventário/risco como área especializada;
- PR, DE, RS e RC usam o painel comum já existente;
- Hub com upload, fila/status de processamento, documentos, cobertura e erros compreensíveis;
- Dashboard exibe saúde/recência dos conectores e não apenas uma nota;
- tendência só é exibida quando houver dados semanticamente comparáveis.

Relatórios exportáveis, snapshots imutáveis e histórico regulatório ficam para o pós-MVP.

**Aceite (evidência):** **projeção compartilhada** `GET /api/v1/scoring/workspace` (`WorkspacePostureQuery`, pela fórmula `aegis-score-v1` sobre o framework ATIVO): postura geral e por Função (score anulável + cobertura + contagens + severidade + evidência mais recente) e saúde/recência dos conectores — a MESMA autoridade do Dashboard e das seis Funções, sem recomputo concorrente. **Dashboard** consome AEGIS Score, cobertura, contagens e saúde dos conectores via `PostureSummaryComponent` (banda distinta da **maturidade CMMI/ICR**, rotulada à parte); removida a contagem concorrente `IneffectiveControls`. **Seis Funções** com postura equivalente pelo componente compartilhado (PR/DE/RS/RC no painel comum, GV no Document Hub, **ID no Inventário + seção comum de controles/checklist `ID.*`**); `NotEvaluated` NUNCA vira 0%. **Document Hub** (upload/fila/status/cobertura/erro sanitizado/vazio) completo e reaproveitado, com a **postura GV recarregada após ações** (sync/upload/reanálise/remoção/cobertura). **Saúde dos conectores** operacional só entre **habilitados** (nunca-sincronizado ≠ saudável; desabilitado fora do denominador; expõe configured/enabled/disabled; Dashboard mostra `healthy/enabled`). **Estados** loading/vazio/erro + **retry** em todas as Funções; sem fallback de demonstração; troca de tenant cancela leituras + recarrega (reuso do `TenantContextService`). **Sem migration** (`has-pending-model-changes` limpo; 24 migrations). Backend **537/537** (+9 testes focados da projeção); `dotnet build` 0 erros/0 warnings; `ng build` aprovado (4 warnings de budget CSS conhecidos). ⚠️ O **smoke autenticado das seis rotas** não foi executado (rotas atrás de `authGuard`, sem credenciais no ambiente) — o boot em `/login` foi verificado sem erro de console; a cobertura E2E autenticada fica para a **Entrega 5**. **Sem credenciais, segredos ou dados reais.**

### Entrega 5 — release candidate demonstrável

**Status:** ✅ CONCLUÍDA — PR #21 (squash-merge `5a2f7a8`); `main` local/remota sincronizadas; branch `chore/mvp-release-candidate` removida (local e remota).<br>
**AUD principal:** AEGIS-AUD-048.

Escopo:

- health check de liveness sem dependências externas;
- readiness de PostgreSQL, migrations e dependências indispensáveis;
- configuração de Development/demonstração sem segredo versionado;
- smoke test do roteiro completo com dados sintéticos;
- validação de um tenant e troca entre dois tenants;
- validação de ingestão SIEM e EDR pelo contrato genérico;
- validação das seis Funções, Dashboard, Integrações e Hub;
- correção apenas dos bloqueadores encontrados no smoke;
- roteiro curto de instalação e demonstração usando documentação existente.

Essa entrega não inclui observabilidade distribuída, HA, disaster recovery ou hardening completo.

**Aceite (evidência):** PR #21 (squash-merge `5a2f7a8`, árvore idêntica ao head validado `c646565`; branch removida). **health checks** `GET /health/live` (liveness — não toca banco/IA/fornecedor) e `GET /health/ready` (readiness — PostgreSQL + migrations + catálogo/regras pelo `SchemaReadinessGuard`, só leitura; ausência de Azure OpenAI/Entra/conectores NÃO reprova), **anônimos** e com **resposta sanitizada** (só status; sem connection string/stack/detalhe). **Segredo `Aegis@12345` eliminado**: `seed-user` exige `Demo:Password` **em runtime** (env `Demo__Password`/user-secret; sem padrão versionado, nunca logada, não devolvida), e-mail demo `analista@demo.example.com`, restrito a DEBUG+Development; novo `seed-second-tenant` para o 2º ambiente. **Smoke** `scripts/smoke-mvp.ps1` (PowerShell 5.1+, banco descartável isolado, nunca toca `aegis_dev`, segredos só em runtime) — **35/35 gates**: DbMigrator, liveness/readiness, seeds sem segredo, login→seleção→troca entre dois tenants, `X-Tenant` fail-closed (403), conectores Generic SIEM/EDR, ingestão SIEM/EDR + dedup + chave inválida (401), score determinístico, seis Funções, Dashboard, Integrações, Hub, isolamento do 2º tenant. **Bloqueador corrigido:** `index.html` sem `<base href="/">` (tela branca em refresh/deep-link de rota aninhada). **Validação visual** das telas essenciais sem tela quebrada nem erro de console. Backend **540/540** (+3 health checks); `dotnet build` 0/0; `has-pending-model-changes` limpo — **sem migration** (24 migrations); `ng build` aprovado. Roteiro curto em `DEV.md`. **Sem credenciais, segredos ou dados reais.**

---

## 5. Política reduzida de testes

Testes existentes não devem ser apagados. A redução ocorre na repetição das baterias e na criação de
testes novos.

### Durante a implementação

- executar somente build e testes diretamente relacionados ao código alterado;
- não rodar a suíte completa após cada edição ou commit;
- não repetir teste em SHA inalterado;
- não criar testes para comentários, DTOs triviais ou getters sem lógica;
- limitar novos testes, em regra, a 3–8 cenários de maior risco por entrega.

### Antes do merge

| Tipo de mudança | Gate mínimo |
|---|---|
| Somente frontend | `ng build` + smoke dos fluxos alterados |
| Backend sem schema/segurança | build + testes direcionados + suíte completa uma vez |
| Migration, tenant, autenticação ou score | testes direcionados + PostgreSQL real focado + suíte completa uma vez |
| Documentação/handoff | `git diff --check`; sem build repetido |

### Release candidate

Executar uma única bateria consolidada:

- backend completo;
- testes PostgreSQL indispensáveis;
- frontend build;
- smoke E2E do roteiro de demonstração;
- verificação de isolamento entre dois tenants;
- verificação de que nenhum segredo ou dado real entrou no repositório.

Ficam adiados:

- suíte frontend ampla do AEGIS-AUD-033;
- matriz extensa de browsers;
- carga, caos, múltiplas réplicas e testes de DR;
- testes duplicados que provam a mesma invariante em várias camadas.

---

## 6. Backlog pós-MVP

Os achados abaixo não foram descartados. Eles deixam de bloquear a entrega de 30 dias.

### Avaliação, IA e refinamentos de domínio

`AEGIS-AUD-003`, `AEGIS-AUD-004`, `AEGIS-AUD-005`, `AEGIS-AUD-006`,
`AEGIS-AUD-025`, `AEGIS-AUD-044`, `AEGIS-AUD-045`.

### Arquitetura e hardening de identidade

`AEGIS-AUD-013`, `AEGIS-AUD-014`, `AEGIS-AUD-015`, `AEGIS-AUD-016`,
`AEGIS-AUD-017`.

### Dashboard, relatórios e histórico avançado

`AEGIS-AUD-022`, `AEGIS-AUD-024`, `AEGIS-AUD-034`, `AEGIS-AUD-038`,
`AEGIS-AUD-039`, `AEGIS-AUD-040`. (`AEGIS-AUD-035/036/037` CONCLUÍDOS no PR #26 — snapshots auditáveis; `AEGIS-AUD-034` **CONCLUÍDO** no PR #27 (squash `b9cbcba`) — exportação executiva PDF/CSV a partir do snapshot imutável; `AEGIS-AUD-040` **PARCIAL** — relatório derivado da mesma projeção publicada atendido, vínculo do Dashboard ao vivo com snapshot é resíduo (não bloqueia homologação).)

### Neutralidade, UX e extensibilidade adicionais

`AEGIS-AUD-023`, `AEGIS-AUD-028`, `AEGIS-AUD-029`, `AEGIS-AUD-042`,
`AEGIS-AUD-047`.

### Qualidade e operação em escala

`AEGIS-AUD-033`, `AEGIS-AUD-049`, `AEGIS-AUD-051`, `AEGIS-AUD-054`,
`AEGIS-AUD-055`, `AEGIS-AUD-056`, `AEGIS-AUD-058`.

### Produção, privacidade e continuidade

`AEGIS-AUD-059`, `AEGIS-AUD-060`, `AEGIS-AUD-061`, `AEGIS-AUD-062`,
`AEGIS-AUD-063`.

Essa classificação cobre todos os achados não concluídos que ficaram fora do caminho crítico. Um item
pós-MVP só volta ao plano de 30 dias se surgir como bloqueador comprovado de segurança, integridade dos
dados ou funcionamento do roteiro de demonstração.

---

## 7. Regras de execução para ganhar velocidade

1. No máximo cinco PRs após o PR #16.
2. Um PR entrega um fluxo vertical utilizável; não um AUD isolado.
3. Leitura inicial deve se limitar aos arquivos diretamente envolvidos.
4. Não produzir relatório de investigação separado antes de implementar.
5. Não pedir autorização intermediária para decisões reversíveis dentro do escopo aprovado.
6. Parar somente diante de:
   - risco de perda de dados;
   - segredo real;
   - exposição cross-tenant;
   - migration sem caminho seguro;
   - divergência material de escopo.
7. Não abrir PR exclusivamente documental.
8. Atualizar `AEGIS_STATE.md` e este plano somente após o merge, de forma curta.
9. Não reescrever histórico válido nem repetir testes já associados ao mesmo SHA.
10. Se um refinamento não muda o roteiro de demonstração, movê-lo para o pós-MVP.

---

## 8. Gate de aceite do MVP

O MVP está concluído quando todos os itens abaixo forem demonstrados no mesmo ambiente:

- [ ] banco preparado e API pronta;
- [ ] login funcional;
- [ ] seleção e troca de tenant sem vazamento de estado;
- [ ] conector configurado com segredo protegido;
- [ ] ingestão autenticada de exemplo SIEM;
- [ ] ingestão autenticada de exemplo EDR;
- [ ] evidências persistidas e visíveis;
- [ ] mapeamento NIST determinístico;
- [ ] score reproduzível e explicável;
- [ ] não avaliado diferente de zero;
- [ ] Dashboard com score, cobertura, riscos, recência e saúde dos conectores;
- [ ] GV, ID, PR, DE, RS e RC com conteúdo, controles e checklist;
- [ ] Document Hub com upload, processamento, cobertura e erro visível;
- [ ] frontend sem fallback silencioso para demo;
- [ ] smoke test completo aprovado;
- [ ] nenhum segredo ou dado identificável versionado.

### Dependências externas que não são defeito de código

- App Registration e consentimento do Microsoft Entra ID para login federado real;
- credenciais de um SIEM/EDR real, se for desejado validar um adaptador específico;
- conectividade do notebook corporativo com PostgreSQL, APIs externas e os endpoints configurados.

Sem essas credenciais, o aceite de código usa o contrato genérico autenticado e payloads sintéticos. O
repositório nunca deve receber credenciais reais.

---

## 9. Limite da entrega

Ao final dos 30 dias, o AEGIS pode ser declarado:

**“MVP funcional e pronto para demonstração/homologação controlada.”**

Ainda não deve ser declarado:

**“Plataforma MSSP pronta para produção irrestrita.”**

Produção completa continuará dependendo do backlog pós-MVP, especialmente observabilidade, CI/CD,
hardening, privacidade, retenção, múltiplas réplicas e continuidade.

---

## 10. Uso com Claude e Codex

Ao iniciar uma sessão:

1. Ler `AEGIS_STATE.md` e este plano.
2. Confirmar branch, SHA e alterações locais.
3. Trabalhar na próxima **entrega vertical** da seção 4.
4. Investigar somente o necessário para implementar.
5. Priorizar código funcionando, integração e UX.
6. Aplicar a política reduzida de testes da seção 5.
7. Parar antes do merge para revisão.
8. Após o merge, atualizar somente os dois handoffs existentes.

Nenhum agente deve voltar automaticamente ao modelo antigo de executar os 63 achados em sequência.

---

**Fim do Plano Diretor de Remediação v1.0.3**
