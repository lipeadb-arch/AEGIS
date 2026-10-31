#!/usr/bin/env bash
# [AEGIS-KNIGHT-MEMORY-BUDGET] Reprodução combinada: API REAL (migrator + host inteiro) e módulos OFICIAIS da imagem no MESMO contêiner de
# 512 MiB sem swap. Aquece a API por HTTP com dados sintéticos, dispara as importações no processo da API e mede contêiner, API, filho e heap.
# Uso: IMAGE=aegis-homolog:ci FIXTURES=<psmodules> scripts/ci/knight-memory-combined.sh <cpus|none> [memoria]
# PSI_INJECT=vazio|ilegivel|invalido cobre memory.pressure dentro do contêiner, para medir a política sem PSI válido.
# PSI_INJECT=perdido começa com PSI válido e o retira depois do início do primeiro adaptador, com a coleta em andamento.
# CARGA_EXTERNA=1 faz outro processo do contêiner (pwsh) alocar e tocar 8 MiB a cada 2 s, até 96 MiB, para ver qual proteção age primeiro.
#   É alocação sintética: não representa sessão, comando nem volume da Microsoft.
# ESPERADO=completo (padrão) exige as quatro importações e o percurso inteiro. ESPERADO=interrompido exige MemoryLimit como única causa,
#   leituras anteriores preservadas quando a interrupção ocorre durante a leitura e nada apresentado como coleta completa. Nos dois, a API precisa ter ficado saudável e sem OOM.
# Não usa locatário nem credencial real. Sai com 0 só quando o resultado efetivo é o esperado.
set -uo pipefail

CPUS="${1:-0.5}"
MEMORY="${2:-512m}"
IMAGE="${IMAGE:-aegis-homolog:ci}"
FIXTURES="${FIXTURES:?diretorio dos modulos de fixture}"
ESPERADO="${ESPERADO:-completo}"
case "$ESPERADO" in completo|interrompido) ;; *) echo "ESPERADO desconhecido"; exit 1 ;; esac
SUFFIX="$$"
NET="aegis-mem-net-$SUFFIX"; PG="aegis-mem-pg-$SUFFIX"; APP="aegis-mem-app-$SUFFIX"
PORT="${PORT:-18080}"
BASE="http://127.0.0.1:$PORT"
HDR='X-Forwarded-Proto: https'
WORK="$(mktemp -d)"
RESULT=1

cleanup() {
  docker rm -f "$APP" "$PG" >/dev/null 2>&1 || true
  docker network rm "$NET" >/dev/null 2>&1 || true
  sudo rm -rf "$WORK" 2>/dev/null || rm -rf "$WORK" || true
}
trap cleanup EXIT

# Compara o resultado efetivo com o esperado, a partir do log da API. Preenche PROBLEMAS; vazio significa conforme.
verificar_etapas() {
  # Here-string, não pipe: com pipefail, grep -q fecha o pipe ao achar a linha e o printf falho inverte o resultado.
  PROBLEMAS=""
  problema() { PROBLEMAS="$PROBLEMAS  - $1"$'\n'; }
  exige() { grep -qxF -- "$1" <<<"$LOGS" || problema "faltou: $1"; }
  local etapas falhas encerrados recusas inicios validos nulos
  etapas="$(grep -E '^combinado ' <<<"$LOGS" || true)"
  falhas="$(grep -E '^combinado [^:]+: FALHA' <<<"$etapas" || true)"
  encerrados="$(grep -c 'encerrado para preservar a API' <<<"$LOGS" || true)"
  recusas="$(grep -c 'não iniciado por falta de memória' <<<"$LOGS" || true)"
  inicios="$(grep -oE 'knight-memoria-inicio .*travamento=[^ ]*' <<<"$LOGS" | sed 's/.*travamento=//' || true)"
  validos="$(grep -cE '^[0-9]+([.,][0-9]+)?$' <<<"$inicios" || true)"
  nulos="$(grep -cxF '(null)' <<<"$inicios" || true)"

  # A condição do cenário precisa ter valido: sem isso, um resultado verde não diz nada sobre a política.
  [ -n "$inicios" ] || problema "nenhum knight-memoria-inicio no log"
  case "${PSI_INJECT:-}" in
    "") [ "$nulos" = "0" ] || problema "PSI real deveria estar válido em todo início ($nulos sem leitura)" ;;
    vazio|ilegivel|invalido) [ "$validos" = "0" ] || problema "PSI injetado não valeu ($validos inícios com leitura válida)" ;;
    perdido)
      grep -qE '^[0-9]' <<<"${inicios%%$'\n'*}" || problema "o primeiro início deveria ter PSI válido"
      [ "${inicios##*$'\n'}" = "(null)" ] || problema "o último início deveria estar sem PSI"
      grep -q 'encerrado para preservar a API.*TravamentoAvg10=(null)' <<<"$LOGS" \
        || problema "nenhum adaptador encerrado depois da perda do PSI" ;;
  esac

  if [ "$ESPERADO" = "completo" ]; then
    grep -qx 'memoria-combinada=OK' <<<"$LOGS" || problema "o percurso não terminou com memoria-combinada=OK"
    [ -z "$falhas" ] || problema "etapas com falha: $falhas"
    [ "$encerrados" = "0" ] || problema "$encerrados adaptadores encerrados por memória"
    [ "$recusas" = "0" ] || problema "$recusas adaptadores recusados no início"
    local n a
    for n in 1 2 3; do exige "combinado aquecimento-5000-$n: ok estado=Completed coletadas=12/12"; done
    for a in teams exchange defender purview; do exige "combinado oficial-$a: ok terminação=Completed estágios=start>input>import>imported>result"; done
    exige "combinado fixture-teams: ok estado=Completed coletadas=6/6"
    exige "combinado fixture-exchange-5000: ok estado=Completed coletadas=12/12"
    # Defender e Purview são parciais pela própria fixture: a mesma resposta em toda execução, sem causa de processo ou memória.
    exige "combinado fixture-defender: ok estado=PartialCollection coletadas=14/15"
    exige "combinado fixture-purview: ok estado=PartialCollection coletadas=1/3"
  else
    grep -qx 'memoria-combinada=FALHA' <<<"$LOGS" || problema "interrupção esperada não pode terminar como coleta completa"
    [ -n "$falhas" ] || problema "nenhuma etapa interrompida"
    [ $((encerrados + recusas)) -gt 0 ] || problema "nenhuma proteção de memória agiu"
    local outras
    outras="$(grep -vE '(causas=MemoryLimit|terminação=MemoryLimit estágios=.*)$' <<<"$falhas" || true)"
    [ -z "$outras" ] || problema "causa diferente de MemoryLimit: $outras"
    grep -q 'estado=Completed' <<<"$falhas" && problema "etapa interrompida com estado Completed"
    # Encerrado na importação não há leitura a preservar. Encerrado lendo as caixas do Exchange, as leituras anteriores precisam ficar.
    local durante_leitura
    durante_leitura="$(grep -c 'knight-memoria adaptador=Exchange Online terminacao=MemoryLimit estagio=read ExchangeMailbox' <<<"$LOGS" || true)"
    PRESERVADAS="$(grep -cE 'estado=PartialCollection coletadas=[1-9][0-9]*/[0-9]+ causas=MemoryLimit$' <<<"$falhas" || true)"
    [ "$PRESERVADAS" -ge "$durante_leitura" ] \
      || problema "$durante_leitura coletas interrompidas durante a leitura, só $PRESERVADAS preservaram as leituras já feitas"
  fi
}

measure() { # rotulo: contadores do cgroup e do processo da API, medidos de dentro do contêiner
  docker exec "$APP" sh -c '
    pid=1
    echo "medicao '"$1"': memory.current=$(cat /sys/fs/cgroup/memory.current) memory.peak=$(cat /sys/fs/cgroup/memory.peak 2>/dev/null || echo ?)"
    grep -E "^(anon|file|shmem|active_file|inactive_file|file_mapped|kernel|slab) " /sys/fs/cgroup/memory.stat | tr "\n" " "; echo
    grep -E "^(VmRSS|VmHWM|RssAnon|RssFile|RssShmem):" /proc/$pid/status | tr -s " \t" " " | tr "\n" " "; echo
    tr "\n" " " < /sys/fs/cgroup/memory.events; echo
    echo "psi: $(tr "\n" " " < /sys/fs/cgroup/memory.pressure 2>/dev/null || echo indisponivel)"' 2>&1 | sed "s/^/  /"
}

docker network create "$NET" >/dev/null || exit 1
PGPASS="$(openssl rand -hex 16)"; PFXPASS="$(openssl rand -hex 16)"; JWTKEY="$(openssl rand -base64 48)"
ADMIN_EMAIL="admin@ci.example.com"; ADMINPASS="ci memoria $(openssl rand -hex 8)"

docker run -d --name "$PG" --network "$NET" -e POSTGRES_PASSWORD="$PGPASS" -e POSTGRES_DB=aegis_mem postgres:18 >/dev/null || exit 1
for _ in $(seq 1 60); do docker exec "$PG" pg_isready -U postgres -d aegis_mem >/dev/null 2>&1 && break; sleep 1; done

openssl req -x509 -newkey rsa:2048 -keyout "$WORK/key.pem" -out "$WORK/cert.pem" -days 2 -nodes -subj "/CN=aegis-ci" >/dev/null 2>&1 || exit 1
openssl pkcs12 -export -out "$WORK/dp.pfx" -inkey "$WORK/key.pem" -in "$WORK/cert.pem" -passout pass:"$PFXPASS" || exit 1
base64 "$WORK/dp.pfx" > "$WORK/dp.pfx.b64"
sudo chown root:1000 "$WORK/dp.pfx.b64"; sudo chmod 0640 "$WORK/dp.pfx.b64"
SECRET_PATH=/etc/secrets/aegis-dataprotection.pfx.b64

# O cache de página é cobrado de quem toca o arquivo primeiro. Sem esvaziá-lo, DLLs da API e dos módulos ficam na conta do `docker build`
# e o contêiner parece menor do que é em produção, onde a primeira leitura acontece dentro do cgroup.
sync; echo 3 | sudo tee /proc/sys/vm/drop_caches >/dev/null || { echo "não foi possível esvaziar o cache de página"; exit 1; }

CPU_ARG=(); [ "$CPUS" != "none" ] && CPU_ARG=(--cpus="$CPUS")
PSI_ARG=()
case "${PSI_INJECT:-}" in
  "") ;;
  vazio) : > "$WORK/psi" ;;
  ilegivel) : > "$WORK/psi"; sudo chown root:root "$WORK/psi"; sudo chmod 000 "$WORK/psi" ;;
  invalido) echo 'full avg10=NaN avg60=0.00 avg300=0.00 total=0' > "$WORK/psi" ;;
  perdido) echo 'full avg10=0.00 avg60=0.00 avg300=0.00 total=0' > "$WORK/psi" ;;
  *) echo "PSI_INJECT desconhecido"; exit 1 ;;
esac
[ -n "${PSI_INJECT:-}" ] && PSI_ARG=(-v "$WORK/psi:/sys/fs/cgroup/memory.pressure:ro")
echo "== contêiner: memória=$MEMORY sem swap, cpus=$CPUS psi=${PSI_INJECT:-real} cargaExterna=${CARGA_EXTERNA:-0} esperado=$ESPERADO (produção usa o mesmo ENTRYPOINT: migrator e depois API)"
docker run -d --name "$APP" --network "$NET" -p "127.0.0.1:$PORT:8080" \
  --memory="$MEMORY" --memory-swap="$MEMORY" "${CPU_ARG[@]}" "${PSI_ARG[@]}" \
  -e "ConnectionStrings__AegisScore=Host=$PG;Port=5432;Database=aegis_mem;Username=postgres;Password=$PGPASS;SSL Mode=Disable" \
  -e "Jwt__SigningKey=$JWTKEY" \
  -e "DataProtection__Certificate__Path=$SECRET_PATH" -e "DataProtection__Certificate__Password=$PFXPASS" \
  -e "Bootstrap__Enabled=true" -e "Bootstrap__AdminEmail=$ADMIN_EMAIL" -e "Bootstrap__AdminPassword=$ADMINPASS" -e "Bootstrap__TenantName=Cliente CI" \
  -e "PORT=8080" \
  -e "Knight__MemoryCheck__FixtureModules=/fixtures" -e "AEGIS_FIXTURE_OFFICIAL_MODULE_ROOT=/opt/aegis/psmodules" \
  -v "$WORK/dp.pfx.b64:$SECRET_PATH:ro" -v "$FIXTURES:/fixtures:ro" \
  --entrypoint /bin/sh "$IMAGE" \
  -c 'dotnet /app/migrator/AegisScore.DbMigrator.dll && exec dotnet /app/AegisScore.Api.dll --knight-memory-combined-check' >/dev/null || exit 1

ready=0
for i in $(seq 1 120); do
  [ "$(docker inspect -f '{{.State.Running}}' "$APP" 2>/dev/null)" = "true" ] || { echo "contêiner encerrou cedo"; break; }
  code="$(curl -s -o /dev/null -w '%{http_code}' -H "$HDR" "$BASE/health/ready" 2>/dev/null || true)"
  [ "$code" = "200" ] && { ready=1; echo "API pronta em ~$((i*2))s"; break; }
  sleep 2
done
if [ "$ready" != "1" ]; then docker logs "$APP" 2>&1 | tail -80; exit 1; fi
measure "api-recem-iniciada"

# ---- Aquecimento por HTTP, com dados sintéticos e os caminhos que pesam na API -----------------------------------------
TOKEN="$(curl -s -H "$HDR" -H 'Content-Type: application/json' \
  -d "{\"email\":\"$ADMIN_EMAIL\",\"password\":\"$ADMINPASS\"}" "$BASE/api/v1/auth/login" | jq -r '.accessToken // empty')"
if [ -z "$TOKEN" ]; then echo "login falhou"; docker logs "$APP" 2>&1 | tail -40; exit 1; fi
TENANT="$(printf '%s' "$TOKEN" | cut -d. -f2 | tr '_-' '/+' | python3 -c "import base64,json,sys; p=sys.stdin.read().strip(); print(json.loads(base64.b64decode(p + '=' * (-len(p) % 4)))['tenant_id'])")"
call() { curl -s -o /dev/null -w "%{http_code} $1 $2\n" -X "$1" -H "$HDR" -H "Authorization: Bearer $TOKEN" -H "X-Tenant: $TENANT" -H 'Content-Type: application/json' ${3:+-d "$3"} "$BASE$2"; }
ROUNDS="${WARM_ROUNDS:-6}"
for r in $(seq 1 "$ROUNDS"); do
  call POST /api/v1/knight/assessments/demo | { [ "$r" = "1" ] && cat || cat >/dev/null; }
  for path in /api/v1/knight/assessments/latest /api/v1/knight/assessments/latest-by-source /api/v1/knight/assessments/consolidated \
              /api/v1/knight/assessments/reference-coverage /api/v1/knight/assessments/glossary /api/v1/knight/assessments/sources \
              /api/v1/posture/snapshots /api/v1/nist/assessments/history /api/v1/nist/assessments/assist/availability; do
    call GET "$path" | { [ "$r" = "1" ] && cat || cat >/dev/null; }
  done
  SNAP="$(curl -s -X POST -H "$HDR" -H "Authorization: Bearer $TOKEN" -H "X-Tenant: $TENANT" -H 'Content-Type: application/json' \
    -d '{"type":"Knight","source":"demo"}' "$BASE/api/v1/posture/snapshots" | tee "$WORK/snap.json" | jq -r '.summary.id // .id // empty' 2>/dev/null)"
  if [ -n "$SNAP" ]; then
    for fmt in pdf html csv; do call GET "/api/v1/posture/snapshots/$SNAP/export?format=$fmt" | { [ "$r" = "1" ] && cat || cat >/dev/null; }; done
  elif [ "$r" = "1" ]; then echo "fotografia sintética não publicada (o aquecimento segue sem exportação): $(head -c 300 "$WORK/snap.json")"; fi
done
measure "api-aquecida"

# ---- API saudável DURANTE as importações ------------------------------------------------------------------------------
( while :; do
    probe="$(curl -s -m 5 -o /dev/null -w '%{http_code} %{time_total}' -H "$HDR" "$BASE/health/ready" 2>/dev/null || true)"
    echo "${probe%% *}" >> "$WORK/health.txt"
    echo "${probe##* }" >> "$WORK/latency.txt"
    sleep 2
  done ) &
HEALTH_PID=$!

echo "== disparo: importações oficiais e percurso com fixture no processo da API"
docker exec "$APP" sh -c 'touch /tmp/aegis-memory-check-go'
if [ "${CARGA_EXTERNA:-0}" = "1" ]; then
  # 8 MiB tocados a cada 2 s, até 96 MiB, somados ao que o próprio pwsh ocupa. Fica vivo até o fim da verificação.
  docker exec -d "$APP" pwsh -NoProfile -Command '$k=@(); 1..12 | % { $b=[byte[]]::new(8MB); [Array]::Fill($b,[byte]1); $k+=,$b; Start-Sleep 2 }; Start-Sleep 900'
fi
done=0
for _ in $(seq 1 450); do
  LOGS="$(docker logs "$APP" 2>&1)"
  if [ "${PSI_INJECT:-}" = "perdido" ] && [ -s "$WORK/psi" ] && [[ "$LOGS" == *knight-memoria-inicio* ]]; then
    # Trunca o mesmo arquivo: o contêiner enxerga a mudança no bind mount enquanto o filho está coletando.
    : > "$WORK/psi"; echo "== PSI retirado durante a coleta"
  fi
  case "$LOGS" in *"memoria-combinada=OK"*|*"memoria-combinada=FALHA"*) done=1; break;; esac
  [ "$(docker inspect -f '{{.State.Running}}' "$APP" 2>/dev/null)" = "true" ] || { echo "contêiner encerrou durante a verificação"; break; }
  sleep 2
done
kill "$HEALTH_PID" 2>/dev/null; wait "$HEALTH_PID" 2>/dev/null

measure "fim"
LOGS="$(docker logs "$APP" 2>&1)"
echo "== etapas"; echo "$LOGS" | grep -E '^combinado |^alcance:|^NÃO PROVADO|^memoria-combinada=' | sed 's/^/  /'
echo "== início por adaptador"; echo "$LOGS" | grep -E 'knight-memoria-inicio|não iniciado por falta' | sed 's/^ *//' | sed 's/^/  /'
echo "== memória por execução do adaptador"; echo "$LOGS" | grep -E 'knight-memoria |encerrado para preservar a API' | sed 's/^ *//' | sed 's/^/  /'

TOTAL="$(wc -l < "$WORK/health.txt" 2>/dev/null || echo 0)"; BAD="$(grep -vc '^200$' "$WORK/health.txt" 2>/dev/null || true)"
RUNNING="$(docker inspect -f '{{.State.Running}}' "$APP" 2>/dev/null || echo false)"
RESTARTS="$(docker inspect -f '{{.RestartCount}}' "$APP" 2>/dev/null || echo '?')"
OOM="$(docker inspect -f '{{.State.OOMKilled}}' "$APP" 2>/dev/null || echo '?')"
KILLS="$(docker exec "$APP" sh -c 'grep "^oom_kill " /sys/fs/cgroup/memory.events | cut -d" " -f2' 2>/dev/null || echo '?')"
SLOWEST="$(sort -n "$WORK/latency.txt" 2>/dev/null | tail -1)"
echo "== saúde: sondagens=$TOTAL fora-de-200=$BAD latenciaMaxima=${SLOWEST:-?}s emExecucao=$RUNNING reinicios=$RESTARTS oomKilled=$OOM oom_kill=$KILLS"
CODE="$(curl -s -m 5 -o /dev/null -w '%{http_code}' -H "$HDR" "$BASE/health/ready" 2>/dev/null || true)"
echo "== /health/ready ao fim: $CODE"

verificar_etapas
[ "$done" = "1" ] || PROBLEMAS="$PROBLEMAS  - a verificação não terminou"$'\n'
[ "$CODE" = "200" ] && [ "$RUNNING" = "true" ] && [ "$RESTARTS" = "0" ] && [ "$OOM" = "false" ] && [ "$KILLS" = "0" ] && [ "$TOTAL" -gt 0 ] && [ "$BAD" = "0" ] \
  || PROBLEMAS="$PROBLEMAS  - API sem saúde ou com OOM"$'\n'

ROTULO="memoria=$MEMORY cpus=$CPUS psi=${PSI_INJECT:-real} cargaExterna=${CARGA_EXTERNA:-0}"
if [ -n "$PROBLEMAS" ]; then
  echo "CENARIO FORA DO ESPERADO ($ESPERADO; $ROTULO)"; printf '%s' "$PROBLEMAS"
  echo "$LOGS" | grep -E ' fail: |crit: |Unhandled|OutOfMemory' | tail -20
elif [ "$ESPERADO" = "completo" ]; then
  echo "CENARIO COMPLETO OK ($ROTULO)"; RESULT=0
else
  echo "CENARIO INTERROMPIDO CONFORME ESPERADO, coleta NÃO completa: MemoryLimit como causa, $PRESERVADAS coletas parciais com leituras preservadas, API saudável, sem OOM ($ROTULO)"; RESULT=0
fi
exit "$RESULT"
