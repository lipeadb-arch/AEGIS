# Aegis Score

Aegis Score é a plataforma com duas abordagens complementares:

- **AEGIS KNIGHT**: assessment técnico de configuração e exposição. Coletores de leitura do Microsoft 365, Azure e Google Workspace; indicadores, evidências, achados e remediação rastreáveis.
- **AEGIS NIST**: avaliação organizacional pelo NIST CSF 2.0. Avaliação → rodada → escopo → funções → subcategorias → evidências → revisão → achados, planos e publicação.

A maturidade NIST usa uma escala autoral de 1 a 5, atual × alvo. A nota do KNIGHT usa seus próprios critérios. As duas medidas não são somadas. A plataforma não oferece uma terceira pontuação global automática.

## Recursos reaproveitados

O **hub de documentos** está em AEGIS NIST → Govern → Documentos. Upload, extração de PDF/DOCX/texto, análise assistida, trechos probatórios e deduplicação continuam disponíveis. Documento analisado não altera automaticamente a maturidade: o analista vincula a evidência à rodada e fundamenta a avaliação.

O **inventário de ativos** está em AEGIS NIST → Identify → Ativos. O inventário existente, filtros, criticidade, proveniência e vínculo como evidência NIST são preservados. Retirar um coletor não apaga ativos já registrados.

Scanners de CVEs do Defender e Google Cloud, integrações SIEM/EDR genéricas, Secure Score, Google SecOps, Sentinel e o coletor independente de postura Intune foram aposentados. Os coletores de configuração do KNIGHT, inclusive **Defender para Office 365** e **Intune**, continuam ativos.

## Estrutura

- `backend/src`: Domain, Application, Infrastructure, conectores Microsoft/Google, API e migrator.
- `backend/tests`: regressões dos produtos, segurança, documentos e compatibilidade de banco.
- `frontend/src/app/pages/nist`: jornada NIST; `pages/aegis-knight.component.ts`: KNIGHT.
- `docs`, `AEGIS_STATE.md`: contexto e histórico de implementação.
- `.config`, `.github`, `.claude`: ferramentas de desenvolvimento e implantação.

Arquivos em `Application/Legacy` e `Infrastructure/Legacy`, entidades antigas e migrations existem para leitura e atualização compatíveis de bancos e relatórios históricos. Não são um terceiro módulo ativo.

## Execução local

Pré-requisitos: .NET 10, Node.js e PostgreSQL. Configure connection string, JWT e Data Protection conforme [DEV.md](DEV.md); segredos ficam fora do Git.

```powershell
dotnet run --project backend/src/AegisScore.DbMigrator -- --environment Development
dotnet run --project backend/src/AegisScore.Api
npm start --prefix frontend
```

O migrator prepara o banco; a API apenas verifica a prontidão. A análise assistida usa a configuração existente de IA; a jornada NIST manual funciona sem um provedor externo.

## Validação

```powershell
dotnet build backend/AegisScore.sln
npm run build --prefix frontend
npm run test:logic --prefix frontend
```

Testes de PostgreSQL exigem uma instância descartável configurada por `AEGIS_TEST_PG`. Não apontar a suíte de integração para o banco operacional. Arquitetura em [ARCHITECTURE.md](ARCHITECTURE.md).
