<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import AppFooter from './AppFooter.vue'
import AppHeader from './AppHeader.vue'
import AppSidebar from './AppSidebar.vue'
import AppToaster from '../components/AppToaster.vue'
import ReauthModal from '../components/ReauthModal.vue'
import { useAppStore } from '../stores/app'

defineProps<{ onRetry: () => void | Promise<void> }>()

const { t } = useI18n()
const app = useAppStore()
</script>

<template>
  <AppSidebar />

  <div class="wrapper d-flex flex-column min-vh-100">
    <AppHeader />

    <div class="body flex-grow-1">
      <CContainer fluid class="px-4">
        <CAlert v-if="app.connectionError" color="danger" class="d-flex align-items-center">
          <CIcon icon="cil-warning" class="flex-shrink-0 me-2" />
          <span class="me-auto">{{ app.connectionError }}</span>
          <CButton color="danger" variant="outline" size="sm" @click="onRetry()">
            {{ t('重新整理') }}
          </CButton>
        </CAlert>

        <div v-if="!app.ready" class="d-flex justify-content-center py-5">
          <CSpinner color="primary" />
        </div>

        <RouterView v-else v-slot="{ Component }">
          <component :is="Component" />
        </RouterView>
      </CContainer>
    </div>

    <AppFooter />
  </div>

  <AppToaster />
  <ReauthModal />
</template>
