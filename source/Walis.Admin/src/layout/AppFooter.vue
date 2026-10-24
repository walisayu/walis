<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { useSettingsStore } from '../stores/settings'

const { t } = useI18n()
const settings = useSettingsStore()
</script>

<template>
  <CFooter class="px-4">
    <div>
      <RouterLink to="/dashboard">{{ settings.settings?.siteName ?? 'Walis' }}</RouterLink>
      <span class="ms-1">© 2026 Walis Ayu.</span>
      <span v-if="settings.version" class="ms-2 text-body-secondary" :title="t('版本')">v{{ settings.version }}</span>
    </div>
    <div class="ms-auto">
      {{ t('版面採用') }}&nbsp;
      <a href="https://coreui.io/vue/docs/" target="_blank" rel="noopener noreferrer">CoreUI for Vue</a>
    </div>
  </CFooter>
</template>
