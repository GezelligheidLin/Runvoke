import { createApp } from 'vue'
import App from './App.vue'
import NotificationWindow from './components/NotificationWindow.vue'
import './style.css'

const isNotificationWindow = Boolean(Reflect.get(window, '__RUNVOKE_NOTIFICATION__'))
createApp(isNotificationWindow ? NotificationWindow : App).mount('#app')

