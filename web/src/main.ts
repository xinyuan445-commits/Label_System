import { createPinia } from 'pinia'
import { createApp } from 'vue'

import App from './App.vue'
import { setUnauthorizedHandler } from './api/client'
import { notifyStartupReady } from './bridge/startup-ready'
import { router } from './router'
import { loadRuntimeConfig } from './services/runtime-config'
import { startDocumentDraftSync } from './services/document-draft-sync'
import { useAuthStore } from './stores/auth'
import './styles/base.css'

const config = await loadRuntimeConfig()
document.documentElement.dataset.environment = config.environment

const app = createApp(App)
const pinia = createPinia()

app.use(pinia)
const authStore = useAuthStore(pinia)

setUnauthorizedHandler(async () => {
  authStore.invalidate()
  if (router.currentRoute.value.name !== 'login') {
    await router.replace({ name: 'login' })
  }
})

app.use(router)
app.mount('#app')
void router.isReady().then(notifyStartupReady)
void authStore.initialize().finally(() => startDocumentDraftSync())
