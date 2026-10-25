import { Routes } from '@angular/router';

// One route component per screen; each feature is a lazy-loaded chunk with its own bundle budget (angular.json).
export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'queues' },
  { path: 'queues', loadComponent: () => import('./features/queues/queues.page').then(m => m.QueuesPage), title: 'Work queues - MERIT' },
  { path: 'held', loadComponent: () => import('./features/held/held.page').then(m => m.HeldPage), title: 'Held records - MERIT' },
  { path: 'results', loadComponent: () => import('./features/results/results.page').then(m => m.ResultsPage), title: 'Results - MERIT' },
  {
    path: 'people/:personId/timeline',
    loadComponent: () => import('./features/timeline/timeline.page').then(m => m.TimelinePage),
    title: 'Timeline - MERIT',
  },
  {
    path: 'match-review',
    loadComponent: () => import('./features/match-review/match-review.page').then(m => m.MatchReviewPage),
    title: 'Person matching review - MERIT',
  },
  { path: 'map', loadComponent: () => import('./features/map/case-map.page').then(m => m.CaseMapPage), title: 'Case map - MERIT' },
  { path: 'clusters', loadComponent: () => import('./features/clusters/clusters.page').then(m => m.ClustersPage), title: 'Clusters - MERIT' },
  {
    path: 'clusters/:clusterId',
    loadComponent: () => import('./features/clusters/clusters.page').then(m => m.ClusterPage),
    title: 'Cluster - MERIT',
  },
  { path: 'reports', loadComponent: () => import('./features/reports/reports.page').then(m => m.ReportsPage), title: 'Reports - MERIT' },
  { path: '**', redirectTo: 'queues' },
];
