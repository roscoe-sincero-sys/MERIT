import { Route, Routes } from '@angular/router';
import { NotPermittedPage, requires } from './shared/not-permitted.page';

/** A screen behind its permissions; without them the same address shows "Not permitted" (issue #161). */
function guarded(route: Route, ...permissions: string[]): Route[] {
  return [{ ...route, canMatch: [requires(...permissions)] }, { path: route.path, component: NotPermittedPage, title: 'Not permitted - MERIT' }];
}

// One route component per screen; each feature is a lazy-loaded chunk with its own bundle budget (angular.json).
export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'queues' },
  ...guarded({ path: 'queues', loadComponent: () => import('./features/queues/queues.page').then(m => m.QueuesPage), title: 'Work queues - MERIT' }, 'task.view'),
  ...guarded({ path: 'held', loadComponent: () => import('./features/held/held.page').then(m => m.HeldPage), title: 'Held records - MERIT' }, 'result.release_hold'),
  ...guarded({ path: 'results', loadComponent: () => import('./features/results/results.page').then(m => m.ResultsPage), title: 'Results - MERIT' }, 'result.view'),
  ...guarded({
    path: 'people/:personId/timeline',
    loadComponent: () => import('./features/timeline/timeline.page').then(m => m.TimelinePage),
    title: 'Timeline - MERIT',
  }, 'person.view'),
  ...guarded({
    path: 'match-review',
    loadComponent: () => import('./features/match-review/match-review.page').then(m => m.MatchReviewPage),
    title: 'Person matching review - MERIT',
  }, 'result.release_hold'),
  ...guarded({ path: 'map', loadComponent: () => import('./features/map/case-map.page').then(m => m.CaseMapPage), title: 'Case map - MERIT' }, 'case.view'),
  ...guarded({ path: 'clusters', loadComponent: () => import('./features/clusters/clusters.page').then(m => m.ClustersPage), title: 'Clusters - MERIT' }, 'case.view'),
  ...guarded({
    path: 'clusters/:clusterId',
    loadComponent: () => import('./features/clusters/clusters.page').then(m => m.ClusterPage),
    title: 'Cluster - MERIT',
  }, 'case.view'),
  ...guarded({ path: 'reports', loadComponent: () => import('./features/reports/reports.page').then(m => m.ReportsPage), title: 'Reports - MERIT' }, 'result.view', 'case.view'),
  { path: '**', loadComponent: () => import('./shared/not-found.page').then(m => m.NotFoundPage), title: 'Page not found - MERIT' },
];
