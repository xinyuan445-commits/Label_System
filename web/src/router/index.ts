import { createRouter, createWebHashHistory } from 'vue-router'

import { hasValidSession } from '@/services/auth'

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
      path: '/',
      name: 'home',
      component: () => import('@/views/HomeView.vue'),
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

  return true
})
