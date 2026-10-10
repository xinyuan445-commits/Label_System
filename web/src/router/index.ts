import { createRouter, createWebHashHistory } from 'vue-router'

import { hasValidSession } from '@/services/auth'
import { isFeatureVisible } from '@/services/feature-visibility'

export const router = createRouter({
  history: createWebHashHistory(),
  routes: [
    {
      path: '/login',
      name: 'login',
      component: () => import('@/views/LoginView.vue'),
      meta: { guestOnly: true },
    },
    {
      path: '/settings',
      name: 'settings',
      component: () => import('@/views/SettingsView.vue'),
    },
    {
      path: '/settings/address',
      name: 'settings-address',
      component: () => import('@/views/AddressSettingsView.vue'),
    },
    {
      path: '/settings/scanner',
      name: 'settings-scanner',
      component: () => import('@/views/ScanTestView.vue'),
    },
    {
      path: '/settings/features',
      name: 'settings-features',
      component: () => import('@/views/FeatureVisibilitySettingsView.vue'),
    },
    {
      path: '/settings/data-sync',
      name: 'settings-data-sync',
      component: () => import('@/views/DataSyncSettingsView.vue'),
    },
    {
      path: '/settings/users',
      name: 'settings-users',
      component: () => import('@/views/UserManagementView.vue'),
    },
    {
      path: '/settings/sound',
      name: 'settings-sound',
      component: () => import('@/views/SoundTestView.vue'),
    },
    {
      path: '/',
      name: 'home',
      component: () => import('@/views/HomeView.vue'),
      meta: { requiresAuth: true },
    },
    {
      path: '/functions',
      name: 'all-functions',
      component: () => import('@/views/AllFunctionsView.vue'),
      meta: { requiresAuth: true },
    },
    {
      path: '/favorites',
      name: 'favorites',
      component: () => import('@/views/FavoriteSettingsView.vue'),
      meta: { requiresAuth: true },
    },
    {
      path: '/work/:code/source',
      name: 'source-document-select',
      component: () => import('@/views/SourceDocumentSelectView.vue'),
      meta: { requiresAuth: true },
    },
    {
      path: '/work/:code/documents',
      name: 'outbound-document-manager',
      component: () => import('@/views/OutboundDocumentManagerView.vue'),
      meta: { requiresAuth: true },
    },
    {
      path: '/work/:code/switches',
      name: 'business-document-settings',
      component: () => import('@/views/BusinessDocumentSettingsView.vue'),
      meta: { requiresAuth: true },
    },
    {
      path: '/work/:code',
      name: 'work-function',
      component: () => import('@/views/BusinessView.vue'),
      meta: { requiresAuth: true },
    },
    {
      path: '/:pathMatch(.*)*',
      redirect: '/',
    },
  ],
})

router.beforeEach((to) => {
  const signedIn = hasValidSession()

  if (to.meta.requiresAuth && !signedIn) {
    return { name: 'login' }
  }

  if (to.meta.guestOnly && signedIn) {
    return { name: 'home' }
  }

  const featureCode = Array.isArray(to.params.code) ? to.params.code[0] : to.params.code
  if (typeof featureCode === 'string' && !isFeatureVisible(featureCode)) {
    return { name: 'home' }
  }

  return true
})
