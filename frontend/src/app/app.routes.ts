import { Routes } from '@angular/router';
import { ExecutiveDashboardComponent } from './pages/executive-dashboard.component';
import { AegisDashboardComponent } from './pages/aegis-dashboard.component';
import { AssetInventoryComponent } from './pages/asset-inventory.component';
import { DocumentHubComponent } from './pages/document-hub.component';
import { ProtectDashboardComponent } from './pages/protect-dashboard.component';
import { AegisKnightComponent } from './pages/aegis-knight.component';
import { PostureHistoryComponent } from './pages/posture-history.component';
import { PostureExposuresComponent } from './pages/posture-exposures.component';
import { VulnerabilitiesComponent } from './pages/vulnerabilities.component';
import { PrioritiesComponent } from './pages/priorities.component';
import { ControlsHubComponent } from './pages/controls-hub.component';
import { DetectDashboardComponent } from './pages/detect-dashboard.component';
import { RespondDashboardComponent } from './pages/respond-dashboard.component';
import { RecoverDashboardComponent } from './pages/recover-dashboard.component';
import { LoginComponent } from './pages/login.component';
import { IntegrationsComponent } from './pages/integrations.component';
import { SettingsComponent } from './pages/settings/settings.component';
import { SettingsGeneralComponent } from './pages/settings/settings-general.component';
import { SettingsUsersComponent } from './pages/settings/settings-users.component';
import { SettingsTenantsComponent } from './pages/settings/settings-tenants.component';
import { authGuard } from './guards/auth.guard';
import { tenantAdminGuard } from './guards/tenant-admin.guard';
import { platformAdminGuard } from './guards/platform-admin.guard';

export const routes: Routes = [
  { path: 'login', component: LoginComponent, title: 'Aegis · Entrar' },

  { path: '', redirectTo: 'dashboard', pathMatch: 'full' },
  { path: 'dashboard', component: ExecutiveDashboardComponent, canActivate: [authGuard], title: 'Aegis · Visão geral' },
  // [AEGIS-MVP-PRODUCT-01] Tendência de postura: o conteúdo ÚTIL do antigo "HUD" continua aqui, agora sob
  // Relatórios. A rota é a MESMA — nenhum link antigo quebra —, mas ela deixou de ser uma segunda entrada
  // ambígua de dashboard no menu principal.
  { path: 'aegis-score', component: AegisDashboardComponent, canActivate: [authGuard], title: 'Aegis · Tendência de Postura' },
  // [AEGIS-MVP-PRODUCT-01] Governança e controles: porta ÚNICA das seis Funções NIST. A navegação por Função
  // passou a ser INTERNA a esta tela; as rotas de cada Função seguem existindo e acessíveis por link direto.
  { path: 'controls', component: ControlsHubComponent, canActivate: [authGuard], title: 'Aegis · Governança e controles' },
  { path: 'assets', component: AssetInventoryComponent, canActivate: [authGuard], title: 'Aegis · Inventário de Ativos' },
  { path: 'governance', component: DocumentHubComponent, canActivate: [authGuard], title: 'Aegis · Central de Documentos (Govern)' },
  { path: 'protect', component: ProtectDashboardComponent, canActivate: [authGuard], title: 'Aegis · Protect (PR)' },
  // Rota /identity MANTIDA para evitar churn; a tela agora é o AEGIS KNIGHT (assessment de identidade/exposição).
  { path: 'identity', component: AegisKnightComponent, canActivate: [authGuard], title: 'AEGIS KNIGHT' },
  // Histórico auditável COMPARTILHADO entre AEGIS Score/NIST e AEGIS KNIGHT (fotografias imutáveis + comparação).
  { path: 'history', component: PostureHistoryComponent, canActivate: [authGuard], title: 'Aegis · Histórico de Postura' },
  // [AEGIS-MVP-PRIORITIES-01] Central operacional de prioridades — compõe postura + exposições + vulnerabilidades.
  { path: 'priorities', component: PrioritiesComponent, canActivate: [authGuard], title: 'Aegis · Central de Prioridades' },
  // [AEGIS-MVP-POSTURE-02] Exposições de configuração (postura cloud-first) — Microsoft Secure Score real.
  { path: 'exposures', component: PostureExposuresComponent, canActivate: [authGuard], title: 'Aegis · Exposições de Configuração' },
  // [AEGIS-MVP-VULN-01] Vulnerabilidades (ativo×CVE) multicloud — Microsoft Defender como primeira fonte.
  { path: 'vulnerabilities', component: VulnerabilitiesComponent, canActivate: [authGuard], title: 'Aegis · Vulnerabilidades' },
  { path: 'detect', component: DetectDashboardComponent, canActivate: [authGuard], title: 'Aegis · Detect (DE)' },
  { path: 'respond', component: RespondDashboardComponent, canActivate: [authGuard], title: 'Aegis · Respond (RS)' },
  { path: 'recover', component: RecoverDashboardComponent, canActivate: [authGuard], title: 'Aegis · Recover (RC)' },
  {
    // Shell de Configurações com abas (Geral, Usuários e acessos, Integrações). As rotas administrativas
    // são guardadas por tenantAdminGuard (visibilidade NÃO substitui o backend). /settings → /settings/general.
    path: 'settings',
    component: SettingsComponent,
    canActivate: [authGuard],
    title: 'Aegis · Configurações',
    children: [
      { path: '', redirectTo: 'general', pathMatch: 'full' },
      { path: 'general', component: SettingsGeneralComponent, title: 'Aegis · Configurações · Geral' },
      {
        path: 'users',
        component: SettingsUsersComponent,
        canActivate: [tenantAdminGuard],
        title: 'Aegis · Usuários e acessos',
      },
      {
        path: 'integrations',
        component: IntegrationsComponent,
        canActivate: [tenantAdminGuard],
        title: 'Aegis · Integrações',
      },
      {
        // [AEGIS-MVP-ADMIN-LIFECYCLE-01] Administração de AMBIENTES — autoridade GLOBAL (PlatformAdmin).
        path: 'tenants',
        component: SettingsTenantsComponent,
        canActivate: [platformAdminGuard],
        title: 'Aegis · Ambientes',
      },
    ],
  },
  { path: '**', redirectTo: 'dashboard' },
];
