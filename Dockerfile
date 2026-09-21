# syntax=docker/dockerfile:1

# =============================================================================
# AEGIS Score — imagem ÚNICA de homologação (SPA Angular + API .NET 10 + migrator)
#
# Multi-stage: (1) build do Angular em produção, (2) publish da API e do DbMigrator,
# (3) runtime enxuto ASP.NET Core 10, não-root, com fonte Unicode para o PDF.
#
# NÃO contém segredo, certificado nem connection string — tudo chega por variável de
# ambiente / secret file da hospedagem em runtime. O PostgreSQL é EXTERNO (ex.: Neon
# com TLS); NUNCA há banco dentro deste container.
# =============================================================================

# ---- Stage 1: Angular (produção — apiBase relativo via fileReplacements) -----
FROM node:22-bookworm-slim AS frontend
WORKDIR /src/frontend
# Camada de dependências primeiro (cache de build): só reinstala quando o lockfile muda.
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
# defaultConfiguration=production → troca environment.ts por environment.production.ts (apiBase '').
RUN npm run build

# ---- Stage 2: publish da API e do DbMigrator (.NET 10) ----------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src
COPY global.json ./
COPY backend/ ./backend/
# Empacota o SPA compilado no wwwroot da API ANTES do publish: o Web SDK inclui o wwwroot no output,
# e a API o serve same-origin (UseStaticFiles + MapFallbackToFile no Program.cs).
COPY --from=frontend /src/frontend/dist/aegis-score-frontend/browser/ ./backend/src/AegisScore.Api/wwwroot/
RUN dotnet publish backend/src/AegisScore.Api/AegisScore.Api.csproj -c Release -o /app/api \
    && dotnet publish backend/src/AegisScore.DbMigrator/AegisScore.DbMigrator.csproj -c Release -o /app/migrator

# ---- Stage 2b: runtime do PowerShell + módulo OFICIAL do Microsoft Teams ----
#
# [AEGIS-KNIGHT-COVERAGE-02] A configuração do Microsoft Teams do locatário NÃO tem leitura na versão estável
# (v1.0) do Microsoft Graph: o caminho oficial é o módulo Microsoft Teams PowerShell, com autenticação de
# APLICATIVO. Por isso a imagem carrega um runtime de PowerShell 7 e o módulo PRÉ-INSTALADO.
#
# Duas decisões deliberadas:
#   • o módulo é baixado em TEMPO DE BUILD, numa versão FIXADA, e a imagem roda OFFLINE — nada é buscado da
#     galeria em tempo de execução, e a versão que a homologação validar é a que vai para produção;
#   • a origem é a imagem OFICIAL do PowerShell no mesmo registro das imagens .NET (mcr.microsoft.com), com
#     tag LTS fixada — não há download de binário solto nem script de instalação de terceiro.
#
# Sem este estágio a imagem continua subindo: o coletor do Teams declara a falha de transporte ("o runtime do
# PowerShell não pôde ser iniciado"), os controles de Teams ficam NÃO AVALIADOS com o motivo, e nada mais é
# afetado. O que ele nunca faz é devolver coleta vazia como se fosse ambiente sem problema.
FROM mcr.microsoft.com/powershell:lts-7.4-debian-12 AS teamsps
ARG TEAMS_MODULE_VERSION=7.9.0
ENV TEAMS_MODULE_VERSION=${TEAMS_MODULE_VERSION}
# Aspas SIMPLES de propósito: o `$env:` é do PowerShell e não pode ser expandido pelo shell do build.
RUN pwsh -NoLogo -NoProfile -NonInteractive -Command \
      'Set-PSRepository -Name PSGallery -InstallationPolicy Trusted; \
       New-Item -ItemType Directory -Force -Path /opt/aegis/psmodules | Out-Null; \
       Save-Module -Name MicrosoftTeams -RequiredVersion $env:TEAMS_MODULE_VERSION -Path /opt/aegis/psmodules -Repository PSGallery -ErrorAction Stop; \
       Get-ChildItem /opt/aegis/psmodules/MicrosoftTeams | Select-Object -ExpandProperty Name'

# ---- Stage 3: runtime enxuto, não-root -------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Fonte Unicode para o PDF executivo (acentos pt-BR). O PdfReportFontResolver localiza DejaVuSans.ttf sob
# /usr/share/fonts; fonts-dejavu-core traz Regular+Bold (cobertura latina completa) — o itálico é simulado.
RUN apt-get update \
    && apt-get install -y --no-install-recommends fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/*

# [AEGIS-KNIGHT-COVERAGE-02] Runtime do PowerShell 7 e o módulo oficial do Teams, ambos vindos do estágio
# anterior. O runtime da imagem oficial é autocontido (traz o próprio .NET), então copiar a pasta basta.
#
# A pasta de instalação VARIA com a tag (a LTS instala em `7-lts`, a comum em `7`), então o diretório inteiro é
# copiado e o executável é LOCALIZADO — em vez de um caminho fixo que quebra ao trocar de tag. E o binário é
# EXECUTADO aqui mesmo: se faltar alguma dependência nativa nesta imagem base, o build falha agora, e não numa
# coleta em produção.
COPY --from=teamsps /opt/microsoft/powershell /opt/microsoft/powershell
COPY --from=teamsps /opt/aegis/psmodules /opt/aegis/psmodules
RUN set -eux; \
    PWSH="$(find /opt/microsoft/powershell -maxdepth 2 -name pwsh -type f | head -n1)"; \
    test -n "$PWSH"; \
    ln -sf "$PWSH" /usr/bin/pwsh; \
    /usr/bin/pwsh -NoLogo -NoProfile -NonInteractive -Command '$PSVersionTable.PSVersion.ToString()'

WORKDIR /app
COPY --from=backend /app/api ./
COPY --from=backend /app/migrator ./migrator

# Diretório de documentos gravável pelo usuário não-root (o LocalDocumentStorage escreve aqui). Efêmero:
# para PERSISTIR uploads entre deploys, monte um disco e aponte DocumentStorage__RootPath para ele.
RUN mkdir -p /app/document-store && chown -R $APP_UID:0 /app/document-store

# [Render secret files] Os secret files são montados em /etc/secrets/<arquivo> com o GRUPO 1000. Para o
# usuário não-root (app, uid 1654) LER o PKCS#12 do Data Protection sem rodar como root e sem tornar o
# arquivo público, ele precisa pertencer a esse grupo. O certificado NÃO é copiado para a imagem — só o
# acesso de leitura em runtime é habilitado. Ref.: render.com/docs/docker-secrets
RUN set -eux; \
    getent group 1000 >/dev/null || groupadd -g 1000 rendersecrets; \
    usermod -aG "$(getent group 1000 | cut -d: -f1)" app

# Production para a API (ASPNETCORE_ENVIRONMENT) e para o migrator (DOTNET_ENVIRONMENT). ASPNETCORE_URLS é
# o binding PADRÃO; se a hospedagem injetar $PORT, o Program.cs o respeita e sobrepõe este valor.
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    DocumentStorage__RootPath=/app/document-store

# [AEGIS-KNIGHT-COVERAGE-02] Adaptador de coleta do Microsoft Teams: executável e módulo PRÉ-INSTALADOS nesta
# imagem. São configuração do AMBIENTE — o locatário nunca escolhe executável, comando ou destino. Telemetria e
# verificação de atualização do PowerShell ficam desligadas: a coleta roda offline (sem nenhuma chamada de rede
# além da do próprio locatário) e sem escrever no diretório do usuário não-root.
ENV Knight__Teams__Executable=/usr/bin/pwsh \
    Knight__Teams__ModulePath=/opt/aegis/psmodules \
    POWERSHELL_TELEMETRY_OPTOUT=1 \
    POWERSHELL_UPDATECHECK=Off

# Usuário não-root já provido pela imagem base (uid 1654).
USER $APP_UID
EXPOSE 8080

# Sequência de boot: DbMigrator PRIMEIRO (migrations + seed + verificação + bootstrap opcional do 1º admin);
# só com exit 0 o `&&` deixa a API subir. Se o migrator falhar, o container encerra com o código dele
# (fail-closed — a API nunca sobe sobre banco não preparado). `exec` entrega o PID 1 à API (SIGTERM correto).
ENTRYPOINT ["/bin/sh", "-c", "dotnet /app/migrator/AegisScore.DbMigrator.dll && exec dotnet /app/AegisScore.Api.dll"]
