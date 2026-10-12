import { defineStore } from 'pinia'
import { computed, ref } from 'vue'

import {
  clearSession,
  readSession,
  restoreSession,
  signIn,
  signOut,
  type AuthSession,
  type SignInResult,
} from '@/services/auth'
import { refreshLabelCodeRuleCatalog } from '@/services/label-code-rules'

export const useAuthStore = defineStore('auth', () => {
  const session = ref<AuthSession | null>(readSession())
  const isAuthenticated = computed(() => session.value !== null)

  async function initialize(): Promise<void> {
    session.value = await restoreSession()
    if (session.value) void refreshLabelCodeRuleCatalog().catch(() => undefined)
  }

  async function login(username: string, password: string): Promise<SignInResult> {
    const result = await signIn(username, password)

    if (result.success && result.session) {
      session.value = result.session
      void refreshLabelCodeRuleCatalog().catch(() => undefined)
    }

    return result
  }

  async function logout(): Promise<void> {
    try {
      await signOut()
    } finally {
      session.value = null
    }
  }

  function invalidate(): void {
    clearSession()
    session.value = null
  }

  return {
    session,
    isAuthenticated,
    initialize,
    login,
    logout,
    invalidate,
  }
})
