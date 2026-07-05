import { Routes } from '@angular/router';
import { ExecutiveDashboardComponent } from './pages/executive-dashboard.component';
import { AssetInventoryComponent } from './pages/asset-inventory.component';
import { DocumentHubComponent } from './pages/document-hub.component';

export const routes: Routes = [
  { path: '', redirectTo: 'dashboard', pathMatch: 'full' },
  { path: 'dashboard', component: ExecutiveDashboardComponent, title: 'Aegis · Dashboard Executivo' },
  { path: 'assets', component: AssetInventoryComponent, title: 'Aegis · Inventário de Ativos' },
  { path: 'governance', component: DocumentHubComponent, title: 'Aegis · Central de Documentos (Govern)' },
  { path: '**', redirectTo: 'dashboard' },
];
