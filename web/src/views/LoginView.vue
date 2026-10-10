<script setup lang="ts">
import { ref } from 'vue'
import { useRouter } from 'vue-router'

import { useAuthStore } from '@/stores/auth'
import {
  createDefaultShellConfig,
  loadShellConfig,
  saveShellConfig,
  type ShellConfig,
} from '@/services/shell-config'

const router = useRouter()
const authStore = useAuthStore()

const username = ref('admin')
const password = ref('admin')
const errorMessage = ref('')
const submitting = ref(false)
const settingsOpen = ref(false)
const settingsLoading = ref(false)
const settingsSaving = ref(false)
const settingsError = ref('')
const shellConfig = ref<ShellConfig>(createDefaultShellConfig())

async function openSettings(): Promise<void> {
  settingsOpen.value = true
  settingsLoading.value = true
  settingsError.value = ''

  try {
    shellConfig.value = await loadShellConfig()
  } catch (error) {
    settingsError.value = error instanceof Error ? error.message : '读取失败'
  } finally {
    settingsLoading.value = false
  }
}

function closeSettings(): void {
  if (!settingsSaving.value) {
    settingsOpen.value = false
  }
}

async function handleSettingsSave(): Promise<void> {
  if (settingsSaving.value) {
    return
  }

  settingsSaving.value = true
  settingsError.value = ''

  try {
    await saveShellConfig(shellConfig.value)
    settingsOpen.value = false
  } catch (error) {
    settingsError.value = error instanceof Error ? error.message : '保存失败'
  } finally {
    settingsSaving.value = false
  }
}

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
      <button class="settings-button" type="button" aria-label="设置" @click="openSettings">
        设置
      </button>

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

    <div v-if="settingsOpen" class="settings-backdrop" @click.self="closeSettings">
      <section class="settings-panel" role="dialog" aria-modal="true" aria-labelledby="settings-title">
        <header class="settings-header">
          <h2 id="settings-title">地址</h2>
          <button type="button" class="close-button" aria-label="关闭" @click="closeSettings">×</button>
        </header>

        <div v-if="!settingsLoading" class="environment-list">
          <label class="environment-card">
            <span class="environment-name">
              <input v-model="shellConfig.environment" type="radio" value="development" />
              开发
            </span>
            <input v-model="shellConfig.developmentUrl" class="url-input" inputmode="url" />
          </label>

          <label class="environment-card">
            <span class="environment-name">
              <input v-model="shellConfig.environment" type="radio" value="test" />
              测试
            </span>
            <input v-model="shellConfig.testUrl" class="url-input" inputmode="url" />
          </label>

          <label class="environment-card">
            <span class="environment-name">
              <input v-model="shellConfig.environment" type="radio" value="production" />
              生产
            </span>
            <input v-model="shellConfig.productionUrl" class="url-input" inputmode="url" />
          </label>
        </div>

        <div v-else class="settings-loading">加载中</div>
        <p class="settings-error" aria-live="polite">{{ settingsError }}</p>
        <button
          class="primary-button"
          type="button"
          :disabled="settingsLoading || settingsSaving"
          @click="handleSettingsSave"
        >
          {{ settingsSaving ? '切换中' : '切换' }}
        </button>
      </section>
    </div>
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
  position: relative;
  width: min(100%, 440px);
  padding: 36px;
  border: 1px solid rgba(23, 54, 93, 0.08);
  border-radius: 24px;
  background: rgba(255, 255, 255, 0.96);
  box-shadow: 0 22px 60px rgba(23, 54, 93, 0.14);
}

.settings-button {
  position: absolute;
  top: 20px;
  right: 20px;
  padding: 8px 10px;
  border: 0;
  background: transparent;
  color: #56708f;
  font-size: 14px;
  cursor: pointer;
}

.brand {
  display: flex;
  align-items: center;
  gap: 16px;
  padding-right: 52px;
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

.settings-backdrop {
  position: fixed;
  inset: 0;
  z-index: 10;
  display: grid;
  place-items: center;
  padding: 20px;
  background: rgba(17, 34, 57, 0.42);
}

.settings-panel {
  width: min(100%, 460px);
  padding: 24px;
  border-radius: 20px;
  background: #fff;
  box-shadow: 0 22px 70px rgba(16, 36, 62, 0.28);
}

.settings-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 18px;
}

.settings-header h2 {
  color: #17365d;
  font-size: 22px;
}

.close-button {
  width: 38px;
  height: 38px;
  border: 0;
  border-radius: 10px;
  background: #f0f4f8;
  color: #42526a;
  font-size: 26px;
  line-height: 1;
}

.environment-list {
  display: grid;
  gap: 12px;
}

.environment-card {
  display: grid;
  gap: 10px;
  padding: 14px;
  border: 1px solid #dce5f0;
  border-radius: 14px;
  background: #f9fbfd;
}

.environment-name {
  display: flex;
  align-items: center;
  gap: 8px;
  color: #24344d;
  font-weight: 700;
}

.environment-name input {
  width: 18px;
  height: 18px;
  accent-color: #2368c4;
}

.url-input {
  width: 100%;
  height: 44px;
  padding: 0 12px;
  border: 1px solid #d3deeb;
  border-radius: 10px;
  background: #fff;
  color: #17233d;
  font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
  font-size: 13px;
  outline: none;
}

.url-input:focus {
  border-color: #3378cf;
  box-shadow: 0 0 0 3px rgba(51, 120, 207, 0.1);
}

.settings-loading {
  min-height: 210px;
  display: grid;
  place-items: center;
  color: #6b7d93;
}

.settings-error {
  min-height: 20px;
  margin: 10px 0;
  color: #bf3434;
  font-size: 14px;
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
