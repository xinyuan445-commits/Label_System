import { defineStore } from 'pinia'
import { computed, ref } from 'vue'

import {
  clearSession,
  readSession,
  signIn,
  type AuthSession,
  type SignInResult,
} from '@/services/auth'

export const useAuthStore = defineStore('auth', () => {
  const session = ref<AuthSession | null>(readSession())
  const isAuthenticated = computed(() => session.value !== null)

  async function login(username: string, password: string): Promise<SignInResult> {
    const result = await signIn(username, password)

    if (result.success && result.session) {
      session.value = result.session
    }

    return result
  }

  function logout(): void {
    clearSession()
    session.value = null
  }

  return {
    session,
    isAuthenticated,
    login,
    logout,
  }
})
