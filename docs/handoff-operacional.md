# AEGIS — Handoff Operacional

> Documento de handoff para novas sessões de agentes de programação.
>
> Última atualização: 2026-07-21
>
> **Este documento é um RESUMO OPERACIONAL. Ele não substitui, e não tem precedência sobre:**
> **o código local e o estado Git do repositório**, o `AEGIS_STATE.md` (snapshot arquitetural
> histórico, na raiz), o `docs/pr0-baseline.md` (linha de base técnica) nem o Plano Diretor de
> Remediação. Em qualquer divergência, este arquivo é o elo mais fraco da cadeia — os outros vencem.
> Toda nova sessão deve confirmar o estado local com comandos de leitura antes de modificar arquivos.

---

## 1. Propósito deste arquivo

O `docs/handoff-operacional.md` existe para manter continuidade entre sessões do Claude Code e
outros agentes, dando o contexto mínimo para começar a trabalhar sem reler todo o repositório.

Use este arquivo para entender rapidamente:

- estado do repositório na última atualização registrada;
- trabalho concluído;
- próximo pacote;
- restrições operacionais;
- riscos conhecidos;
- critérios de parada;
- documentos que devem ser lidos antes de qualquer alteração.

Este documento é operacional e deve ser atualizado após cada merge relevante.

### 1.1 Não confundir com o `AEGIS_STATE.md`

O repositório mantém **dois** documentos de estado, com papéis distintos e não intercambiáveis:

| Arquivo | Papel | Característica |
|---|---|---|
| `AEGIS_STATE.md` (raiz) | **Snapshot Arquitetural Tático** — memória histórica longa, organizada por arcos de trabalho | Extenso; registro de longo prazo |
| `docs/handoff-operacional.md` (este) | **Handoff operacional** — resumo enxuto para iniciar sessões | Curto; ponto de partida |

**Nenhum dos dois substitui o outro.** Não copie um sobre o outro: o `AEGIS_STATE.md` contém
histórico que não existe aqui, e sobrescrevê-lo destrói informação.

---

## 2. Fontes de verdade

A ordem de confiança é:

1. repositório local, arquivos atuais e estado Git verificado por comando;
2. `docs/pr0-baseline.md`;
3. Pull Requests e commits já integrados no GitHub;
4. `AEGIS_Plano_Diretor_Remediacao_v1.0.1.md`;
5. `AEGIS_STATE.md` (snapshot arquitetural histórico);
6. este `docs/handoff-operacional.md`.

Documentos não são presumidos atualizados. Em caso de divergência, não invente a resposta:
registre a inconsistência, apresente a evidência e pare antes de modificar código.

---

## 3. Estado Git — como verificar

**Este documento não afirma qual é o estado atual da árvore de trabalho.** Qualquer afirmação
escrita de que a árvore está "limpa" se invalida no instante em que alguém edita um arquivo —
inclusive este. O estado corrente é sempre o que os comandos abaixo retornarem **agora**.

Antes de iniciar qualquer pacote, confirmar com comandos somente de leitura:

```bash
pwd
git rev-parse --show-toplevel
git branch --show-current
git rev-parse HEAD
git status --short
git status --branch
git rev-parse --abbrev-ref --symbolic-full-name @{upstream}
git rev-list --left-right --count HEAD...@{upstream}
```

Se houver mudanças locais, conflito, merge, rebase, cherry-pick ou branch inesperada,
**parar antes de continuar** e reportar o achado.

### 3.1 Referências conhecidas (constatação histórica)

Verificado após o merge do PR #1, em 2026-07-20:

- repositório: `lipeadb-arch/AEGIS`;
- branch de referência: `main`;
- commit da `main` naquele momento: `c3a0bd32e4ace892f26e46e506d0017fdc15b2ce` (`c3a0bd3`);
- divergência com `origin/main` naquele momento: `0 0`;
- branch do PR 0 removida local e remotamente: `chore/pr0-baseline`;
- branch local antiga preservada e fora do escopo:
  - `feat/telemetry-ingestion-scoring-consolidation`.

A branch antiga não deve ser alterada, removida, resetada ou integrada sem autorização específica.

---

## 4. Trabalho concluído

### Pacote PR 0 — Baseline técnica

Status do pacote: **CONCLUÍDO**

Pull Request do GitHub usado para executar o pacote:

```text
#1
```

Commit de merge:

```text
c3a0bd3 — docs: adiciona linha de base tecnica do PR 0 (#1)
```

Artefato criado no repositório:

```text
docs/pr0-baseline.md
```

Escopo realizado:

- documentação da linha de base técnica;
- registro da stack;
- comandos de reprodução;
- resultados de build;
- resultados de testes;
- warnings conhecidos;
- limitações do ambiente;
- ausência de CI/CD, containers e automações observadas.

O PR 0 não corrigiu achados arquiteturais ou de segurança. Ele apenas estabeleceu uma baseline
reproduzível.

> Nota de rastreabilidade: o `docs/pr0-baseline.md` cita `1efc80e` — foi o **commit-base sobre o
> qual a validação técnica foi executada** (HEAD da branch `chore/pr0-baseline`). O squash-merge do
> PR #1 produziu `c3a0bd3`, acrescentando o próprio `docs/pr0-baseline.md`. Entre esses commits, a
> única diferença versionada é a inclusão desse documento; o código-fonte validado permaneceu
> inalterado. `c3a0bd3` é a referência canônica após o merge
> (`git diff --name-status 1efc80e c3a0bd3`).

---

## 5. Linha de base técnica conhecida

### Backend

- plataforma: .NET 10;
- `global.json`: SDK `10.0.100`, com `rollForward` para `latestMinor`;
- SDK utilizado na validação local: `10.0.300`;
- build: aprovado;
- erros: `0`;
- warning conhecido: `1` aviso `CS8604`;
- testes: `219/219` aprovados.

> Pendência técnica conhecida: os projetos direcionam `net10.0`, mas a camada de dados permanece
> na linha EF Core `8.0.x` / Npgsql `8.0.x`. Não é falha (build e testes passam), é desalinhamento.
> É o objeto do `AEGIS-TECH-001` — ver seção 6.

### Frontend

- Angular `19.2`;
- TypeScript `5.7`;
- Node utilizado na validação local: `24.16`;
- npm utilizado na validação local: `11.13`;
- build: aprovado;
- warnings conhecidos: `4` avisos de orçamento de CSS de componentes;
- `npm ci` não foi executado na validação original;
- para clone limpo, a reprodução recomendada é:

```bash
cd frontend
npm ci
npm run build
```

### Automação e infraestrutura

Na baseline não foram identificados:

- pipeline de CI/CD;
- Dockerfile;
- docker-compose;
- infraestrutura de containers;
- execução local validada da API contra PostgreSQL;
- suíte formal de testes frontend;
- lint frontend configurado.

Essas ausências continuam sendo riscos ou limitações até correção específica.

**Ferramentas fora do repositório:** o que estiver instalado apenas na máquina do desenvolvedor
(ferramentas globais do `dotnet`, Docker, `psql`) **não faz parte da baseline do projeto** e não
deve ser presumido em outra máquina nem em CI. Dependência de ferramenta deve ser declarada no
repositório para ser confiável.

---

## 6. Sequência planejada de pacotes

A ordem abaixo é a sequência aprovada. Não pular etapas nem fundir pacotes.

> **A aprovação da ordem não autoriza a implementação.** Ter a sequência aprovada significa apenas
> que os pacotes serão executados nesta ordem. **Cada pacote continua exigindo aprovação explícita
> própria** para sair de `PLANEJADO` e entrar em implementação — investigar e planejar nunca
> equivale a autorização para alterar código.

| # | Pacote | Estado |
|---|---|---|
| 1 | **Reconciliação documental** — este handoff, cabeçalho do `AEGIS_STATE.md`, `.gitignore`, nota de rastreabilidade na baseline | EM EXECUÇÃO |
| 2 | **`AEGIS-TECH-001`** — Alinhamento do backend com .NET 10 e EF Core 10 | INVESTIGADO / PLANEJADO |
| 3 | **Atualização da baseline** e dos documentos operacionais após o TECH-001 | PLANEJADO |
| 4 | **`AEGIS-AUD-053`** — Persistência e proteção do Data Protection Key Ring | PLANEJADO |

### 6.1 `AEGIS-TECH-001` — .NET 10 / EF Core 10

Branch planejada: `chore/tech-001-net10-efcore10`

Escopo: atualização coordenada de EF Core runtime/Design/SQLite, do provider Npgsql, dos pacotes
Microsoft efetivamente vinculados à plataforma, criação de manifesto local de ferramentas
(`dotnet-ef`), correção dos comentários de `.csproj` que ficarem incorretos e apenas as correções
mínimas exigidas por breaking changes.

Fora de escopo: qualquer item do `AEGIS-AUD-053`, mudança funcional de schema, refatoração do
`DbContext`, reescrita de migrations históricas.

**Riscos que governam este pacote:**

- a partir do EF Core 9, `MigrateAsync()` lança exceção se houver mudanças de modelo pendentes em
  relação ao model snapshot — e o boot da API faz *fail-fast*. O snapshot está em
  `ProductVersion 8.0.6`;
- os testes usam `EnsureCreated()` sobre SQLite e **não exercitam migrations**: `219/219` verde
  não prova que as migrations aplicam;
- o `ProductVersion` das migrations históricas é metadado imutável — **não reescrever**.

**Pré-requisito de merge:** Docker/PostgreSQL disponíveis para validar migrations em banco vazio e
a atualização de banco criado pela versão anterior. Gate não executável **não** pode ser declarado
aprovado.

### 6.2 `AEGIS-AUD-053` — Data Protection Key Ring

Severidade: **BLOQUEADOR para produção**. Branch planejada: `fix/aud-053-data-protection-keyring`.

Só deve ser iniciado após a conclusão dos itens 1 a 3. Pontos a investigar quando chegar a vez:

- onde `AddDataProtection()` é configurado e onde é consumido;
- quais dados dependem das chaves e qual purpose é utilizado;
- se existe `SetApplicationName()`;
- onde o key ring é armazenado;
- impacto de restart, container e múltiplas réplicas;
- proteção das chaves em repouso;
- compatibilidade com ciphertext existente;
- alternativas de persistência;
- plano de testes, implantação e rollback.

---

## 7. Contexto arquitetural do produto

O AEGIS é uma plataforma multi-tenant de diagnóstico de postura de segurança baseada no
NIST CSF 2.0.

Características esperadas:

- autenticação corporativa;
- usuários com associação a múltiplos tenants;
- diagnóstico por tenant;
- seis funções do NIST CSF 2.0;
- categorias e subcategorias;
- evidências e pendências;
- scoring;
- assistência por IA;
- dashboard executivo;
- relatórios de diagnóstico;
- integrações com múltiplos fornecedores;
- neutralidade tecnológica e de fornecedor.

Fornecedores previstos incluem, sem limitar o produto a eles:

- Microsoft Entra;
- Microsoft Defender;
- Microsoft Sentinel;
- Google Workspace;
- Google Security Operations;
- AWS;
- outros conectores futuros.

A arquitetura não deve introduzir acoplamento desnecessário a um único fornecedor.

---

## 8. Princípios obrigatórios

Toda alteração deve:

- ser pequena, isolada, testável e reversível;
- preservar interfaces públicas, salvo autorização explícita;
- evitar refatorações fora do escopo;
- preservar compatibilidade de dados;
- considerar multi-tenancy;
- não registrar segredos ou dados sensíveis;
- não reduzir controles de autorização;
- não confundir compilação com conclusão;
- incluir testes de regressão;
- incluir plano de implantação;
- incluir rollback;
- informar incertezas;
- parar diante de ambiguidades críticas.

Nunca inventar:

- arquivos;
- classes;
- métodos;
- APIs;
- dependências;
- ambientes;
- cloud provider;
- requisitos de infraestrutura.

---

## 9. Riscos relevantes ainda abertos

Os 63 achados do Plano Diretor permanecem abertos, exceto marcos operacionais explicitamente
concluídos.

Prioridades críticas conhecidas incluem:

- `AEGIS-AUD-019` — IA não deve emitir veredito autoritativo sem controle adequado;
- `AEGIS-AUD-021` e `AEGIS-AUD-040` — projeções executivas concorrentes;
- `AEGIS-AUD-035` — snapshots de auditoria;
- `AEGIS-AUD-050` — filas duráveis;
- `AEGIS-AUD-052` — migrations e seed na inicialização concorrente da API;
- `AEGIS-AUD-053` — Data Protection Key Ring;
- `AEGIS-AUD-056` — CI/CD;
- `AEGIS-AUD-057` — credencial trivial em configuração;
- `AEGIS-AUD-060` — gestão de segredos;
- `AEGIS-AUD-063` — continuidade.

Não marcar achados como resolvidos apenas porque foram documentados.

---

## 10. Documentos obrigatórios para nova sessão

Ler nesta ordem:

```text
docs/handoff-operacional.md        (este arquivo — ponto de partida, resumo)
AEGIS_STATE.md                     (snapshot arquitetural histórico)
docs/pr0-baseline.md               (linha de base técnica)
AEGIS_Plano_Diretor_Remediacao_v1.0.1.md
```

Depois, ler os arquivos do código relacionados ao pacote atual — e confirmar o estado Git real
antes de qualquer alteração.

O Plano Diretor define:

- prioridades;
- epics;
- gates;
- dependências;
- critérios de aceite;
- definição de pronto.

O `docs/pr0-baseline.md` define:

- stack;
- comandos de reprodução;
- resultados técnicos;
- limitações conhecidas.

O `AEGIS_STATE.md` define:

- histórico arquitetural detalhado por arco de trabalho;
- decisões técnicas anteriores e seu contexto.

Este `docs/handoff-operacional.md` define:

- estado operacional resumido;
- trabalho concluído;
- próximo passo;
- restrições da sessão.

---

## 11. Fluxo de trabalho obrigatório

Para cada pacote:

1. verificar estado Git;
2. ler documentos de contexto;
3. investigar o código;
4. apresentar evidências;
5. propor plano;
6. parar para aprovação;
7. criar branch apenas após aprovação;
8. implementar mudanças pequenas;
9. executar testes;
10. mostrar diff;
11. parar para revisão antes de commit, quando solicitado;
12. commit e push somente com autorização;
13. abrir PR;
14. revisão arquitetural e de segurança;
15. merge;
16. atualizar `main`;
17. remover branch;
18. atualizar o Plano Diretor;
19. atualizar este `docs/handoff-operacional.md` e, quando houver mudança arquitetural relevante,
    o `AEGIS_STATE.md`.

---

## 12. Como atualizar este arquivo após cada merge

Após cada PR mergeado, atualizar:

- data;
- commit da `main` resultante;
- PR concluído;
- pacote concluído;
- arquivos alterados;
- testes executados;
- warnings novos ou removidos;
- risco residual;
- achados encerrados;
- achados ainda abertos;
- próximo pacote;
- branches locais preservadas;
- decisões arquiteturais tomadas;
- limitações e pendências.

Não sobrescrever o histórico sem registrar o que mudou. Ao registrar estado de árvore de trabalho,
usar sempre formulação datada e verificável ("verificado em <data>, HEAD <commit>"), nunca uma
afirmação permanente.

---

## 13. Estado resumido na última atualização

```text
PR 0: concluído via GitHub PR #1
main: c3a0bd3 (verificado em 2026-07-20, sincronizada 0/0 com origin/main)
baseline: docs/pr0-baseline.md
Plano Diretor: v1.0.1
pacote em execução: reconciliação documental
próximo pacote: AEGIS-TECH-001 (.NET 10 / EF Core 10) — investigado e planejado; implementação aguarda aprovação explícita
estado da árvore de trabalho: NÃO PRESUMIR — confirmar com `git status`
```

---

## 14. Regra de parada para a nova sessão

Ao concluir a investigação de um pacote:

- não implementar sem aprovação explícita;
- não criar branch antes da aprovação;
- não fazer commit;
- não fazer push;
- não abrir PR.

Apresentar diagnóstico, evidências, alternativas, recomendação, plano de implementação, plano de
testes, implantação e rollback. Depois, parar e aguardar aprovação explícita.

Parar também, a qualquer momento, diante de: possível perda de dados ou de key ring, risco de
exposição cross-tenant, migration irreversível sem backup, divergência crítica entre código local
e `main`, segredo real em código/log/fixture, falha de teste não explicada, alteração fora do
escopo, ausência de rollback para mudança crítica, ou incompatibilidade com ciphertext, snapshot
ou contrato publicado.
