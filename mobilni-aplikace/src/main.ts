import { createApp } from 'vue';
import { createPinia } from 'pinia';
import App from './application/App.vue';
import { router } from './application/router';
import './application/application.css';
import './application/consumer-brand.css';
import './application/wns-v2.css';
import { installAndroidLifecycle } from './platform/androidLifecycle';

createApp(App).use(createPinia()).use(router).mount('#app');
void installAndroidLifecycle(router);
