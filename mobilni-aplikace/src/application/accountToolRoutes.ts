import type { RouteRecordRaw } from 'vue-router';

/** Account links and the router share the same permission requirements. */
export const accountToolRoutes: RouteRecordRaw[] = [
  { path: '/testing-sessions', name: 'testing-sessions', component: () => import('../features/testing-sessions/TestingSessionsView.vue'), meta: { title: 'Testovací relace', private: true, requiresAccount: true, tester: true } },
  { path: '/persona-lab', name: 'persona-lab', component: () => import('../features/persona-lab/PersonaLabView.vue'), meta: { title: 'AI laboratoř', private: true, requiresAccount: true, lab: true } },
  { path: '/admin/customer-sessions', name: 'customer-admin', component: () => import('../features/customer/CustomerAdminView.vue'), meta: { title: 'Zákaznické relace', private: true, requiresAccount: true, staff: true, operation: true } },
];
