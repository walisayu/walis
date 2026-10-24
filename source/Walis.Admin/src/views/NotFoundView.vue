<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'

const { t } = useI18n()
const router = useRouter()
</script>

<template>
  <CCard class="mb-4">
    <CCardBody class="text-center py-5">
      <h1 class="display-6">404</h1>
      <p class="text-body-secondary">{{ t('找不到頁面') }}</p>
      <CButton color="primary" @click="router.push('/dashboard')">{{ t('回到總覽') }}</CButton>
    </CCardBody>
  </CCard>
</template>
