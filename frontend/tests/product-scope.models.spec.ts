import { PROVIDERS, MICROSOFT_HUB_SERVICES, GENERIC_PROVIDERS } from '../src/app/models/connector.models';
function assert(ok: boolean, message: string): void { if (!ok) throw new Error(message); }
assert(PROVIDERS.every((p) => p.capability === 'IdentityPosture'), 'Catálogo oferece somente coletores KNIGHT');
assert(PROVIDERS.some((p) => p.key === 'GoogleWorkspaceKnight'), 'Google Workspace continua disponível');
assert(MICROSOFT_HUB_SERVICES.length === 1 && MICROSOFT_HUB_SERVICES[0].key === 'IdentityPosture', 'Hub Microsoft sem scanner CVE ou serviço legado');
assert(GENERIC_PROVIDERS.length === 1 && GENERIC_PROVIDERS[0].key === 'GoogleWorkspaceKnight', 'Sem integrações genéricas sem consumidor');
console.log('Escopo da plataforma: 4/4');
