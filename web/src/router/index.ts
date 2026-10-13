import { createRouter, createWebHashHistory } from 'vue-router'

import { hasValidSession, readSession } from '@/services/auth'
import { isFeatureVisible } from '@/services/feature-visibility'
import { hasManagementAccess, requestManagementAccess } from '@/services/management-access'

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
      meta: { requiresAuth: true, requiresManagement: true },
    },
    {
      path: '/settings/data-sync',
      name: 'settings-data-sync',
      component: () => import('@/views/DataSyncSettingsView.vue'),
      meta: { requiresAuth: true, requiresManagement: true },
    },
    {
      path: '/settings/users',
      name: 'settings-users',
      component: () => import('@/views/UserManagementView.vue'),
      meta: { requiresAuth: true, requiresManagement: true },
    },
    {
      path: '/settings/label-code-rules',
      name: 'settings-label-code-rules',
      component: () => import('@/views/LabelCodeRulesView.vue'),
      meta: { requiresAuth: true, requiresAdmin: true },
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
      path: '/work/:code/source-details',
      name: 'source-document-details',
      component: () => import('@/views/SourceDocumentDetailsView.vue'),
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
      meta: { requiresAuth: true, requiresManagement: true },
    },
    {
      path: '/work/:code/document-settings',
      name: 'document-field-settings',
      component: () => import('@/views/DocumentFieldSettingsView.vue'),
      meta: { requiresAuth: true, requiresAdmin: true },
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

router.beforeEach(async (to) => {
  const signedIn = hasValidSession()

  if (to.meta.requiresAuth && !signedIn) {
    return { name: 'login' }
  }

  if (to.meta.guestOnly && signedIn) {
    return { name: 'home' }
  }

  if (to.meta.requiresManagement) {
    const isAdmin = readSession()?.user.roles.some((role) => role.toLowerCase() === 'admin')
    if (!isAdmin) {
      try {
        if (!await hasManagementAccess()) {
          const password = window.prompt('请输入管理密码')
          if (password === null) return false
          await requestManagementAccess(password)
        }
      } catch (reason) {
        window.alert(reason instanceof Error ? reason.message : '验证失败')
        return false
      }
    }
  }

  if (to.meta.requiresAdmin) {
    const isAdmin = readSession()?.user.roles.some((role) => role.toLowerCase() === 'admin')
    if (!isAdmin) {
      window.alert('仅管理员可操作')
      return { name: 'settings' }
    }
  }

  const featureCode = Array.isArray(to.params.code) ? to.params.code[0] : to.params.code
  if (typeof featureCode === 'string' && !isFeatureVisible(featureCode)) {
    return { name: 'home' }
  }

  return true
})
