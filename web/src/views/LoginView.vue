<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'

import { useAuthStore } from '@/stores/auth'

const router = useRouter()
const authStore = useAuthStore()

const username = ref('admin')
const password = ref('admin')
const errorMessage = ref('')
const submitting = ref(false)

async function handleSubmit(): Promise<void> {
  if (submitting.value) {
    return
  }

  const account = username.value.trim()

  if (!account || !password.value) {
    errorMessage.value = '请输入账号和密码'
    return
  }

  submitting.value = true
  errorMessage.value = ''

  const result = await authStore.login(account, password.value)

  if (!result.success) {
    errorMessage.value = result.message || '登录失败'
    submitting.value = false
    return
  }

  await router.replace({ name: 'home' })
}
</script>

<template>
  <main class="login-page">
    <section class="login-panel" aria-labelledby="login-title">
      <header class="brand">
        <span class="brand-mark" aria-hidden="true">标</span>
        <h1 id="login-title">标签管理系统</h1>
      </header>

      <form class="login-form" @submit.prevent="handleSubmit">
        <label class="field">
          <span>账号</span>
          <input
            v-model="username"
            name="username"
            autocomplete="username"
            inputmode="text"
            @input="errorMessage = ''"
          />
        </label>

        <label class="field">
          <span>密码</span>
          <input
            v-model="password"
            name="password"
            type="password"
            autocomplete="current-password"
            @input="errorMessage = ''"
          />
        </label>

        <p class="form-error" aria-live="polite">{{ errorMessage }}</p>

        <button class="primary-button" type="submit" :disabled="submitting">
          {{ submitting ? '登录中' : '登录' }}
        </button>
      </form>
    </section>
  </main>
</template>

<style scoped>
.login-page {
  min-height: 100dvh;
  display: grid;
  place-items: center;
  padding: 32px 20px;
  background:
    radial-gradient(circle at 100% 0%, rgba(62, 132, 224, 0.2), transparent 36%),
    linear-gradient(160deg, #eef5fd 0%, #f8fafc 58%, #edf1f6 100%);
}

.login-panel {
  width: min(100%, 440px);
  padding: 36px;
  border: 1px solid rgba(23, 54, 93, 0.08);
  border-radius: 24px;
  background: rgba(255, 255, 255, 0.96);
  box-shadow: 0 22px 60px rgba(23, 54, 93, 0.14);
}

.brand {
  display: flex;
  align-items: center;
  gap: 16px;
  margin-bottom: 34px;
}

.brand-mark {
  width: 58px;
  height: 58px;
  display: grid;
  place-items: center;
  border-radius: 16px;
  background: #2368c4;
  box-shadow: 0 12px 28px rgba(35, 104, 196, 0.24);
  color: #fff;
  font-size: 26px;
  font-weight: 750;
}

h1 {
  color: #17365d;
  font-size: clamp(26px, 7vw, 34px);
  line-height: 1.2;
}

.login-form {
  display: grid;
  gap: 20px;
}

.field {
  display: grid;
  gap: 8px;
  color: #42526a;
  font-size: 15px;
  font-weight: 650;
}

.field input {
  width: 100%;
  height: 56px;
  padding: 0 16px;
  border: 2px solid #d9e2ef;
  border-radius: 13px;
  background: #fff;
  color: #17233d;
  font: inherit;
  font-size: 17px;
  font-weight: 500;
  outline: none;
  transition: border-color 160ms ease, box-shadow 160ms ease;
}

.field input:focus {
  border-color: #3378cf;
  box-shadow: 0 0 0 4px rgba(51, 120, 207, 0.12);
}

.form-error {
  min-height: 22px;
  margin: -4px 0 -2px;
  color: #bf3434;
  font-size: 14px;
}

.primary-button {
  height: 56px;
  border: 0;
  border-radius: 13px;
  background: #2368c4;
  color: #fff;
  font-size: 17px;
  font-weight: 700;
  cursor: pointer;
  box-shadow: 0 10px 24px rgba(35, 104, 196, 0.22);
}

.primary-button:active {
  transform: translateY(1px);
}

.primary-button:disabled {
  background: #8db3e2;
  cursor: default;
  box-shadow: none;
}

@media (max-width: 480px) {
  .login-page {
    align-items: start;
    padding-top: 11vh;
  }

  .login-panel {
    padding: 28px 24px;
    border-radius: 20px;
  }
}
</style>
