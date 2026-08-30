# Ingestão genérica de evidências — payloads sintéticos

[AEGIS-AUD-020/041/043] Lotes de exemplo (100% sintéticos: `example.com`, IPs reservados para documentação,
ids fictícios, **sem credenciais**) para o contrato v1 de ingestão de SIEM/EDR.

Endpoint: `POST /api/v1/ingestion/connectors/{connectorId}/events`
Autenticação: header `X-Ingestion-Key` (a chave de ingestão configurada no conector Generic/Siem ou Generic/Edr).

Substitua `CONNECTOR_ID` e `INGESTION_KEY` pelos valores do seu conector genérico.

curl:

```bash
curl -sS -X POST "http://localhost:5100/api/v1/ingestion/connectors/CONNECTOR_ID/events" \
  -H "Content-Type: application/json" \
  -H "X-Ingestion-Key: INGESTION_KEY" \
  --data-binary @siem-batch.example.json
```

PowerShell:

```powershell
Invoke-RestMethod -Method Post `
  -Uri "http://localhost:5100/api/v1/ingestion/connectors/CONNECTOR_ID/events" `
  -Headers @{ "X-Ingestion-Key" = "INGESTION_KEY" } `
  -ContentType "application/json" `
  -InFile "siem-batch.example.json"
```

Reenviar o mesmo lote responde `deduplicated` (idempotência por `eventId`). Um `signalKey` sem mapeamento
conhecido responde 422; chave inválida responde 401 genérico.

> ⚠️ [AEGIS-MVP-SCORE-GUARD-SIEM-01] `siem.alert.highSeverity` é FATO OPERACIONAL/consultivo, não evidência de
> controle: a mera presença ou quantidade de alertas SIEM **não comprova** monitoramento suficiente, cobertura,
> resposta ou contenção e **não concede conformidade nem pontos** no AEGIS Score. Esse mapping de scoring foi
> **aposentado** — por isso o push deste `signalKey` responde **422** (sem mapeamento de scoring aprovado). A
> postura de alertas continua disponível na fotografia operacional do SIEM (Microsoft Sentinel / Google SecOps),
> fora do score. O lote `edr.threat.blocked` (`edr-batch.example.json`) segue sendo evidência determinística válida.
