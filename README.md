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
    AegisScore.Api               ASP.NET Core: Program, controllers, DTOs
data/
  nist_csf_2_0_catalog.json   catálogo NIST CSF 2.0 (6 funções / 22 categorias / 106 subcategorias)
frontend/
  src/   Angular + TypeScript (dashboard executivo, gráficos em SVG nativo)
```

## Pré-requisitos

- [.NET SDK 8](https://dotnet.microsoft.com/download) · [PostgreSQL 14+](https://www.postgresql.org/) · [Node.js 18+](https://nodejs.org/)
- (Opcional) Uma chave de API para o motor de IA — sem ela, os endpoints de IA ficam indisponíveis,
  mas o dashboard e o restante funcionam.

## Backend

1. Suba o PostgreSQL e crie o banco/usuário (ajuste a connection string em `appsettings.json`):

   ```sql
   CREATE USER stars WITH PASSWORD 'stars';
   CREATE DATABASE aegis OWNER stars;
   ```

2. (Opcional) Configure a IA — via `appsettings.json` (seção `Ai`) ou variável de ambiente:

   ```bash
   export Ai__ApiKey="sua-chave"      # modelo padrão: claude-sonnet-4-6 (trocável)
   ```

3. Rode a API:

   ```bash
   cd backend/src/AegisScore.Api
   dotnet restore
   dotnet run
   ```

   Na inicialização o schema é criado (`EnsureCreated`) e o **catálogo NIST CSF 2.0 é semeado**
   automaticamente. Swagger em `http://localhost:5080/swagger` (ajuste a porta conforme o launch profile).

> **Produção:** troque `EnsureCreated()` por **migrations** no `Program.cs`:
> ```bash
> dotnet tool install --global dotnet-ef
> dotnet ef migrations add Initial -p ../AegisScore.Infrastructure -s .
> dotnet ef database update -p ../AegisScore.Infrastructure -s .
> ```
> e use `db.Database.Migrate()`.

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
# defina apiBase e tenantId em src/environments/environment.ts
npm start                # ng serve em http://localhost:5173
npm run build            # build de produção em dist/
```

Sem backend configurado, o dashboard renderiza com **dados de exemplo** (que ecoam os prints da stack
Microsoft) e exibe um aviso. SPA em **Angular** (standalone components); os gráficos — radar de
maturidade, gauge do ICR e barras de gap — são desenhados em **SVG/CSS nativo**, sem biblioteca de chart.

## Status

Fundação arquitetural da **Fase 0/1**: domínio completo, scoring de Maturidade/Risco/ICR, abstrações de
conector e IA, API mínima e dashboard. É a base correta e extensível — não um produto compilado
ponta-a-ponta. Itens a confirmar (faixas de risco, pesos do ICR, prioridade de conectores, LLM)
estão sinalizados em `ARCHITECTURE.md`.
