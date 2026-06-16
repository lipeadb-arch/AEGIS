import { Component } from '@angular/core';
import { ExecutiveDashboardComponent } from './pages/executive-dashboard.component';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [ExecutiveDashboardComponent],
  template: `<app-executive-dashboard />`,
})
export class App {}
