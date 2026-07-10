import { Routes } from '@angular/router';
import { ExecutiveDashboardComponent } from './pages/executive-dashboard.component';
import { AegisDashboardComponent } from './pages/aegis-dashboard.component';
import { AssetInventoryComponent } from './pages/asset-inventory.component';
import { DocumentHubComponent } from './pages/document-hub.component';
import { LoginComponent } from './pages/login.component';
import { authGuard } from './guards/auth.guard';

export const routes: Routes = [
  { path: 'login', component: LoginComponent, title: 'Aegis · Entrar' },

  { path: '', redirectTo: 'dashboard', pathMatch: 'full' },
  { path: 'dashboard', component: ExecutiveDashboardComponent, canActivate: [authGuard], title: 'Aegis · Dashboard Executivo' },
  { path: 'aegis-score', component: AegisDashboardComponent, canActivate: [authGuard], title: 'Aegis · Tendência de Postura' },
  { path: 'assets', component: AssetInventoryComponent, canActivate: [authGuard], title: 'Aegis · Inventário de Ativos' },
  { path: 'governance', component: DocumentHubComponent, canActivate: [authGuard], title: 'Aegis · Central de Documentos (Govern)' },
  { path: '**', redirectTo: 'dashboard' },
];
