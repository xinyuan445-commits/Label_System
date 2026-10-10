import { createPinia } from 'pinia'
import { createApp } from 'vue'

import App from './App.vue'
import { router } from './router'
import { loadRuntimeConfig } from './services/runtime-config'
import './styles/base.css'

const config = await loadRuntimeConfig()
document.documentElement.dataset.environment = config.environment

const app = createApp(App)

app.use(createPinia())
app.use(router)
app.mount('#app')
