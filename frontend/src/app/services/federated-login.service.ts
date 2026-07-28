import { Injectable } from '@angular/core';
import { PublicClientApplication } from '@azure/msal-browser';

/**
 * [AEGIS-AUD-007] Adaptador fino do MSAL Browser para o login corporativo (Entra ID).
 *
 * Decisões de segurança:
 *  - Auth Code + PKCE (padrão do msal-browser); NUNCA implicit flow.
 *  - Cache SOMENTE em memória (`memoryStorage`) — o token externo e qualquer estado do MSAL não tocam
 *    localStorage/sessionStorage, então não sobrevivem a reload nem ficam exfiltráveis por XSS.
 *  - Popup iniciado pelo usuário (clique no botão) — nada de redirect automático no bootstrap.
 *  - Config (authority, clientId, scope) vem da API (config pública), não do bundle: os identificadores
 *    do tenant/app ficam fora do repositório.
 */
@Injectable({ providedIn: 'root' })
export class FederatedLoginService {
  private pca: PublicClientApplication | null = null;
  private key = '';

  /** Inicializa (uma vez por config) o cliente público. Recria se a config mudar. */
  private async ensure(authority: string, clientId: string): Promise<PublicClientApplication> {
    const key = `${authority}|${clientId}`;
    if (!this.pca || this.key !== key) {
      this.pca = new PublicClientApplication({
        auth: {
          clientId,
          authority,
          redirectUri: window.location.origin, // deve constar como SPA redirect URI no App Registration
        },
        cache: { cacheLocation: 'memoryStorage' }, // nunca local/sessionStorage
      });
      await this.pca.initialize();
      this.key = key;
    }
    return this.pca;
  }

  /**
   * Abre o popup do Entra e devolve o ACCESS TOKEN para o scope da API. Iniciado pelo usuário; sem
   * implicit flow. O chamador envia esse token ao endpoint de troca do AEGIS e o descarta em seguida.
   */
  async acquireApiToken(authority: string, clientId: string, scope: string): Promise<string> {
    const pca = await this.ensure(authority, clientId);
    const result = await pca.loginPopup({ scopes: [scope] });
    const token = result?.accessToken;
    if (!token) {
      throw new Error('Entra não retornou um access token para o scope solicitado.');
    }
    return token;
  }
}
