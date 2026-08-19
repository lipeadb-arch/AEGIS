import { canResetLocalPassword } from './users.models';

/**
 * Requisito #11 — "o frontend mostra a ação [Redefinir senha] somente nas condições autorizadas".
 *
 * A regra de visibilidade vive num predicado PURO (`canResetLocalPassword`), então é a autoridade única
 * consumida pelo template e verificada pelo build (tipos). Esta é a tabela-verdade dessa regra.
 *
 * ⚠️ Este projeto Angular NÃO tem runner de teste configurado (sem `tsconfig.spec.json`, sem alvo `test` no
 * angular.json, sem karma/jasmine nas devDependencies) — a validação de frontend do PR é `ng build`. Esta spec
 * segue a convenção `*.spec.ts` e roda assim que um runner (`ng test`) for adicionado; ela documenta e trava a
 * regra. A garantia efetiva HOJE é dupla: o backend impõe a policy de plataforma, e o predicado é type-checked
 * pelo uso no componente. NÃO é executada em CI.
 */
describe('canResetLocalPassword (visibilidade de "Redefinir senha")', () => {
  it('MOSTRA só quando PlatformAdmin, alvo com credencial local e não é a própria conta', () => {
    expect(
      canResetLocalPassword({ viewerIsPlatformAdmin: true, targetHasLocalCredential: true, targetIsSelf: false }),
    ).toBe(true);
  });

  it('ESCONDE para quem não é PlatformAdmin (ex.: TenantAdmin sem autoridade global)', () => {
    expect(
      canResetLocalPassword({ viewerIsPlatformAdmin: false, targetHasLocalCredential: true, targetIsSelf: false }),
    ).toBe(false);
  });

  it('ESCONDE para conta federated-only (sem credencial local a redefinir)', () => {
    expect(
      canResetLocalPassword({ viewerIsPlatformAdmin: true, targetHasLocalCredential: false, targetIsSelf: false }),
    ).toBe(false);
  });

  it('ESCONDE para a própria conta (a própria senha usa a troca normal, que exige a senha atual)', () => {
    expect(
      canResetLocalPassword({ viewerIsPlatformAdmin: true, targetHasLocalCredential: true, targetIsSelf: true }),
    ).toBe(false);
  });

  it('permanece escondida quando falta qualquer condição combinada', () => {
    // Enumeração exaustiva: só a tripla (true, true, !self) libera; qualquer outra combinação esconde.
    for (const viewerIsPlatformAdmin of [false, true]) {
      for (const targetHasLocalCredential of [false, true]) {
        for (const targetIsSelf of [false, true]) {
          const expected = viewerIsPlatformAdmin && targetHasLocalCredential && !targetIsSelf;
          expect(
            canResetLocalPassword({ viewerIsPlatformAdmin, targetHasLocalCredential, targetIsSelf }),
          ).toBe(expected);
        }
      }
    }
  });
});
