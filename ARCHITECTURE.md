# Arquitetura da plataforma Aegis Score

## Produtos e fronteiras

**AEGIS KNIGHT** avalia configurações técnicas e exposição a partir de aquisições identificadas, catálogo versionado e critérios determinísticos. IA é consultiva. Os coletores Microsoft cobrem Entra ID, Teams, Exchange Online, Defender para Office 365, Purview, SharePoint/OneDrive, Intune, Fabric e Azure; o Google Workspace tem seu próprio coletor.

**AEGIS NIST** avalia práticas organizacionais pelo NIST CSF 2.0. Cada registro pertence a avaliação, rodada, escopo e subcategoria. Nível atual, alvo, aplicabilidade, justificativa, procedimentos, observações, evidências e revisão são rastreáveis. Conteúdo herdado/importado aguarda confirmação humana. A escala de maturidade 1–5 é autoral da plataforma, não uma nota definida pelo NIST.

KNIGHT e NIST possuem metodologias e publicações próprias. Evidências técnicas podem fundamentar a revisão organizacional por mapeamento explícito. Não há nota combinada nem pontuação global de conformidade atribuída por telemetria ou documento.

## Documentos e ativos no NIST

A biblioteca de Govern usa armazenamento local configurável, extração de texto/PDF/DOCX, fila PostgreSQL com lease/retry, deduplicação por hash e citações literais validadas. A análise gera evidência disponível: vinculá-la à rodada é decisão explícita do analista. Cobertura documental descreve a documentação existente; não comprova maturidade organizacional.

O inventário de Identify preserva ativos canônicos, filtros, criticidade e proveniência. A associação à avaliação NIST produz uma referência identificada. Históricos coletados por integrações aposentadas continuam legíveis; o inventário não depende de um scanner de CVEs em execução.

## Camadas e execução

API ASP.NET Core → Application → Domain. Infrastructure implementa persistência EF Core/Npgsql, arquivos, IA, filas, consultas e exportação. Microsoft e Google isolam os transportes de coleta. Frontend Angular standalone usa a API autenticada.

PostgreSQL é o armazenamento operacional. `AegisScore.DbMigrator` aplica migrations e seeds sob lock. `SchemaReadinessGuard` apenas verifica a prontidão na API. Filas de coleta KNIGHT e análise/sincronização documental são duráveis. Workers executam no host da API.

Leituras de contexto NIST/Auditor usam `QueryReadSnapshot`: uma transação de leitura consistente, sem dependência do antigo módulo de correlação por CVE.

## Integrações ativas

`ProductConnectorScope` delimita configuração e execução: fontes Microsoft/Google do KNIGHT e integrações de documentos do NIST. O catálogo da interface oferece os coletores KNIGHT reais; upload documental continua independente deles.

Foram retirados os scanners de vulnerabilidade Defender e Google Cloud, Microsoft Secure Score, Sentinel, Google SecOps e postura independente de Intune. Isso não retira os coletores KNIGHT de Defender para Office 365 ou Intune.

APIs antigas de scoring, dashboard operacional, telemetria, ingestão genérica, vulnerabilidades e entrevistas legadas respondem como recursos aposentados. Registros antigos de conectores não são apagados, mas não podem disparar coleta nem voltar ao catálogo ativo.

## Histórico e compatibilidade

Publicações KNIGHT/NIST são imutáveis, com versões, proveniência e hashes. Relatórios históricos permanecem acessíveis. A pontuação global antiga não recebe novas publicações; NIST é publicado pela jornada com avaliação, rodada e escopo.

Entidades, colunas, enums e migrations históricas permanecem para compatibilidade. Código de pontuação necessário a upgrades/leitura histórica fica em `Legacy`, com namespaces preservados. Não alterar nem remover migrations aplicadas como parte de uma limpeza de produto. Nenhuma alteração no banco operacional é necessária para esta reorganização.

## Segurança

Tenant vem da sessão autenticada; filtros e guardas de escrita falham fechados. Conectores guardam segredos cifrados. Papéis, concorrência, autoria, revisão e trilha continuam sendo conferidos no servidor. A assistência de IA não transforma sugestões em decisões sem a operação normal de gravação.

## Diretórios de trabalho

`C:\Projetos\AEGIS` é o checkout principal. As pastas irmãs `AEGIS-wt-*` são worktrees de tarefas; não são projetos necessários para executar a plataforma. Worktrees com handoffs modificados precisam ter essas alterações preservadas antes de remoção pelo Git. `.git` dentro do projeto é necessário; diretórios de agente e ferramentas pertencem ao desenvolvimento.
