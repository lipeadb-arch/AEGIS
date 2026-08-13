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

# ---- Stage 3: runtime enxuto, não-root -------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Fonte Unicode para o PDF executivo (acentos pt-BR). O PdfReportFontResolver localiza DejaVuSans.ttf sob
# /usr/share/fonts; fonts-dejavu-core traz Regular+Bold (cobertura latina completa) — o itálico é simulado.
RUN apt-get update \
    && apt-get install -y --no-install-recommends fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/*

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

# Usuário não-root já provido pela imagem base (uid 1654).
USER $APP_UID
EXPOSE 8080

# Sequência de boot: DbMigrator PRIMEIRO (migrations + seed + verificação + bootstrap opcional do 1º admin);
# só com exit 0 o `&&` deixa a API subir. Se o migrator falhar, o container encerra com o código dele
# (fail-closed — a API nunca sobe sobre banco não preparado). `exec` entrega o PID 1 à API (SIGTERM correto).
ENTRYPOINT ["/bin/sh", "-c", "dotnet /app/migrator/AegisScore.DbMigrator.dll && exec dotnet /app/AegisScore.Api.dll"]
