import { Routes } from '@angular/router';
import { ExecutiveDashboardComponent } from './pages/executive-dashboard.component';
import { AegisDashboardComponent } from './pages/aegis-dashboard.component';
import { AssetInventoryComponent } from './pages/asset-inventory.component';
import { DocumentHubComponent } from './pages/document-hub.component';
import { ProtectDashboardComponent } from './pages/protect-dashboard.component';
import { DetectDashboardComponent } from './pages/detect-dashboard.component';
import { RespondDashboardComponent } from './pages/respond-dashboard.component';
import { RecoverDashboardComponent } from './pages/recover-dashboard.component';
import { LoginComponent } from './pages/login.component';
import { authGuard } from './guards/auth.guard';

export const routes: Routes = [
  { path: 'login', component: LoginComponent, title: 'Aegis · Entrar' },

  { path: '', redirectTo: 'dashboard', pathMatch: 'full' },
  { path: 'dashboard', component: ExecutiveDashboardComponent, canActivate: [authGuard], title: 'Aegis · Dashboard Executivo' },
  { path: 'aegis-score', component: AegisDashboardComponent, canActivate: [authGuard], title: 'Aegis · Tendência de Postura' },
  { path: 'assets', component: AssetInventoryComponent, canActivate: [authGuard], title: 'Aegis · Inventário de Ativos' },
  { path: 'governance', component: DocumentHubComponent, canActivate: [authGuard], title: 'Aegis · Central de Documentos (Govern)' },
  { path: 'protect', component: ProtectDashboardComponent, canActivate: [authGuard], title: 'Aegis · Protect (PR)' },
  { path: 'detect', component: DetectDashboardComponent, canActivate: [authGuard], title: 'Aegis · Detect (DE)' },
  { path: 'respond', component: RespondDashboardComponent, canActivate: [authGuard], title: 'Aegis · Respond (RS)' },
  { path: 'recover', component: RecoverDashboardComponent, canActivate: [authGuard], title: 'Aegis · Recover (RC)' },
  { path: '**', redirectTo: 'dashboard' },
];
