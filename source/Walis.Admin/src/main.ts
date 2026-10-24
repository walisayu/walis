import { createApp } from 'vue'
import { createPinia } from 'pinia'
import CoreuiVue from '@coreui/vue'

import '@coreui/coreui/dist/css/coreui.min.css'
import '@coreui/chartjs/dist/css/coreui-chartjs.css'
import './style.css'

import App from './App.vue'
import AppIcon from './components/AppIcon.vue'
import { router } from './router'
import { i18n } from './i18n'
import { icons } from './icons'

const app = createApp(App)

app.use(createPinia())
app.use(router)
app.use(i18n)
app.use(CoreuiVue)
app.provide('icons', icons)
app.component('CIcon', AppIcon)

app.mount('#app')
