import { Routes } from '@angular/router';
import { ExecutiveDashboardComponent } from './pages/executive-dashboard.component';
import { AegisDashboardComponent } from './pages/aegis-dashboard.component';
import { AssetInventoryComponent } from './pages/asset-inventory.component';
import { DocumentHubComponent } from './pages/document-hub.component';
import { ProtectDashboardComponent } from './pages/protect-dashboard.component';
import { AegisKnightComponent } from './pages/aegis-knight.component';
import { PostureHistoryComponent } from './pages/posture-history.component';
import { PostureExposuresComponent } from './pages/posture-exposures.component';
import { DetectDashboardComponent } from './pages/detect-dashboard.component';
import { RespondDashboardComponent } from './pages/respond-dashboard.component';
import { RecoverDashboardComponent } from './pages/recover-dashboard.component';
import { LoginComponent } from './pages/login.component';
import { IntegrationsComponent } from './pages/integrations.component';
import { SettingsComponent } from './pages/settings/settings.component';
import { SettingsGeneralComponent } from './pages/settings/settings-general.component';
import { SettingsUsersComponent } from './pages/settings/settings-users.component';
import { authGuard } from './guards/auth.guard';
import { tenantAdminGuard } from './guards/tenant-admin.guard';

export const routes: Routes = [
  { path: 'login', component: LoginComponent, title: 'Aegis · Entrar' },

  { path: '', redirectTo: 'dashboard', pathMatch: 'full' },
  { path: 'dashboard', component: ExecutiveDashboardComponent, canActivate: [authGuard], title: 'Aegis · Dashboard Executivo' },
  { path: 'aegis-score', component: AegisDashboardComponent, canActivate: [authGuard], title: 'Aegis · Tendência de Postura' },
  { path: 'assets', component: AssetInventoryComponent, canActivate: [authGuard], title: 'Aegis · Inventário de Ativos' },
  { path: 'governance', component: DocumentHubComponent, canActivate: [authGuard], title: 'Aegis · Central de Documentos (Govern)' },
  { path: 'protect', component: ProtectDashboardComponent, canActivate: [authGuard], title: 'Aegis · Protect (PR)' },
  // Rota /identity MANTIDA para evitar churn; a tela agora é o AEGIS KNIGHT (assessment de identidade/exposição).
  { path: 'identity', component: AegisKnightComponent, canActivate: [authGuard], title: 'AEGIS KNIGHT' },
  // Histórico auditável COMPARTILHADO entre AEGIS Score/NIST e AEGIS KNIGHT (fotografias imutáveis + comparação).
  { path: 'history', component: PostureHistoryComponent, canActivate: [authGuard], title: 'Aegis · Histórico de Postura' },
  // [AEGIS-MVP-POSTURE-02] Exposições de configuração (postura cloud-first) — Microsoft Secure Score real.
  { path: 'exposures', component: PostureExposuresComponent, canActivate: [authGuard], title: 'Aegis · Exposições de Configuração' },
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
    ],
  },
  { path: '**', redirectTo: 'dashboard' },
];
