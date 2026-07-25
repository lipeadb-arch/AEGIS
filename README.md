# Aegis Score — Auditoria de Maturidade Cibernética

Módulo de **auditoria de maturidade cibernética** do portal **Synapse OS**. Motor de IA que
diagnostica de forma contínua a maturidade de Segurança da Informação com base em **NIST CSF 2.0**
e **GRC**, confrontando autodeclaração e políticas (analisadas por IA) com **fatos coletados por API**
das ferramentas do cliente — de forma **vendor-agnostic** (Microsoft, Google, AWS, SIEMs e EDRs).

> Arquitetura e decisões de design completas em [`ARCHITECTURE.md`](./ARCHITECTURE.md).

## O que é

- **Avalia maturidade** (CMMI 1–5) por subcategoria NIST, agrega por categoria/função e calcula **gaps** (atual × alvo).
- **Registra e pontua riscos** (`Probabilidade + Impacto + Valor do Processo`), com matriz e faixas configuráveis.
- Calcula o **ICR** (Índice de Criticidade de Risco Cibernético, 0–100), ponderado e contínuo.
- Usa **IA** para analisar evidências documentais, conduzir entrevistas/questionários, sugerir maturidade,
  gerar planos de ação e relatórios executivos, e **normalizar** a saída bruta de ferramentas desconhecidas.
- Entrega um **dashboard executivo** (maturidade por função, gaps, matriz de risco, ICR, exposição).

## Arquitetura (resumo)

Padrão **Adapter + Facade**: o núcleo nunca fala a língua nativa de um fornecedor; opera sobre um
**esquema JSON unificado** (`EvidenceSignal`). Cada ferramenta entra por um adapter (`IEvidenceConnector`).
Coleta apoiada em open-source: **Osquery** (endpoint/SO), **Steampipe/CloudQuery** (CSPM de nuvem),
leitores de API de SIEM/EDR. Um **LLM** atua como normalizador dinâmico do que não é estruturado.

```
backend/
  AegisScore.sln
  src/
    AegisScore.Domain            entidades, enums, regras puras
    AegisScore.Application       interfaces (IA, conector, tenant) + scoring (Maturidade/Risco/ICR)
    AegisScore.Infrastructure    EF Core (PostgreSQL), seeder NIST, IA (Anthropic), registry
    AegisScore.Connectors.Microsoft  adapter de exemplo (Secure Score)
    AegisScore.DbMigrator        prepara o banco (migrations + seed); a API não migra no boot
    AegisScore.Api               ASP.NET Core: Program, controllers, DTOs
      Data/                      catálogo NIST CSF 2.0 (106 subcats) + regras de avaliação (aegis_assessment_rules.json)
frontend/
  src/   Angular + TypeScript (dashboard executivo, gráficos em SVG nativo)
```

## Pré-requisitos

- [.NET SDK 10](https://dotnet.microsoft.com/download) · [PostgreSQL 14+](https://www.postgresql.org/) · [Node.js 20+](https://nodejs.org/)
- (Opcional) Uma chave de API para o motor de IA — sem ela, os endpoints de IA ficam indisponíveis,
  mas o dashboard e o restante funcionam.

## Backend

1. Suba o PostgreSQL e crie o banco/role de login (a connection string **não** fica no
   `appsettings.json` — ver passo 2):

   ```sql
   CREATE ROLE aegis WITH LOGIN PASSWORD '<defina-uma-senha>';
   CREATE DATABASE aegis OWNER aegis;
   ```

2. Configure os segredos **fora do git** (connection string, chave JWT e, opcionalmente, a chave de IA)
   via `dotnet user-secrets` (Development) ou variáveis de ambiente. O `appsettings.json` versionado
   deixa esses campos vazios de propósito. Passo a passo em [`DEV.md`](./DEV.md).

3. Prepare o banco com o **`AegisScore.DbMigrator`** — é ele quem aplica as migrations e semeia o
   catálogo NIST CSF 2.0. A API **não** migra nem semeia no boot: apenas verifica a prontidão do schema
   e falha rápido se o banco não estiver preparado.

   ```bash
   dotnet run --project backend/src/AegisScore.DbMigrator -- --environment Development
   ```

4. Rode a API:

   ```bash
   dotnet run --project backend/src/AegisScore.Api
   ```

   Swagger em `http://localhost:5100/swagger` (Development). Opções do migrator e solução de problemas
   em [`DEV.md`](./DEV.md).

### Fluxo rápido (cURL)

```bash
# 1) cria o cliente (tenant) -> retorna { "id": "<TENANT>" }
curl -s -X POST localhost:5080/api/v1/tenants -H "Content-Type: application/json" \
  -d '{"name":"Grupo Think","slug":"think"}'

# 2) catálogo NIST ativo
curl -s localhost:5080/api/v1/framework/active

# 3) dashboard executivo (escopado pelo header do tenant)
curl -s localhost:5080/api/v1/dashboard/executive -H "X-Tenant: <TENANT>"
```

Todas as rotas de dados são escopadas por tenant via header **`X-Tenant`** (isolamento multi-cliente).

## Frontend

```bash
cd frontend
npm install
# defina apiBase em src/environments/environment.ts (o tenant vem do JWT, não é configurado aqui)
npm start                # ng serve em http://localhost:5173
npm run build            # build de produção em dist/
```

SPA em **Angular 19** (standalone components + signals); os gráficos — radar de maturidade, gauge do
ICR e barras de gap — são desenhados em **SVG/CSS nativo**, sem biblioteca de chart. O dashboard consome
a API real: quando ela falha, exibe um **estado de erro explícito** (com opção de nova tentativa) — nunca
dados de demonstração no lugar da postura real.

## Status

Fundação arquitetural da **Fase 0/1**: domínio completo, scoring de Maturidade/Risco/ICR, abstrações de
conector e IA, API mínima e dashboard. É a base correta e extensível — não um produto compilado
ponta-a-ponta. Itens a confirmar (faixas de risco, pesos do ICR, prioridade de conectores, LLM)
estão sinalizados em `ARCHITECTURE.md`.
