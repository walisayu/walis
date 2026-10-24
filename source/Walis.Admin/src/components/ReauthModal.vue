<script setup lang="ts">
import { nextTick, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { onReauthRequired } from '../services/api'

const { t } = useI18n()

const visible = ref(false)
const message = ref('')
const password = ref('')
const input = ref<HTMLInputElement | null>(null)
let resolver: ((value: string | null) => void) | null = null

/** One prompt at a time; a second request that needs step-up while the dialog is open waits for the same answer. */
let pending: Promise<string | null> | null = null

function ask(reason: string): Promise<string | null> {
  if (pending) return pending

  message.value = reason
  password.value = ''
  visible.value = true
  pending = new Promise<string | null>((resolve) => {
    resolver = resolve
  })
  void nextTick(() => input.value?.focus())
  return pending
}

function finish(value: string | null): void {
  visible.value = false
  // The password only travels on the retried request; it is not kept in the component.
  password.value = ''
  const resolve = resolver
  resolver = null
  pending = null
  resolve?.(value)
}

function confirm(): void {
  if (password.value) finish(password.value)
}

onMounted(() => onReauthRequired(ask))
</script>

<template>
  <CModal :visible="visible" backdrop="static" @close="finish(null)">
    <CModalHeader>
      <CModalTitle>{{ t('確認你的密碼') }}</CModalTitle>
    </CModalHeader>
    <form @submit.prevent="confirm">
      <CModalBody>
        <p class="mb-3">{{ message || t('這個操作需要再次輸入你的密碼。') }}</p>
        <CFormLabel for="reauth-password">{{ t('目前的密碼') }}</CFormLabel>
        <input
          id="reauth-password"
          ref="input"
          v-model="password"
          class="form-control"
          type="password"
          autocomplete="current-password"
          required
        />
        <CFormText>{{ t('建立 API 金鑰、替他人重設密碼或一次性密碼、授予管理員、關閉登入或稽核、解除鎖定時都會再確認一次。') }}</CFormText>
      </CModalBody>
      <CModalFooter>
        <CButton color="secondary" variant="outline" type="button" @click="finish(null)">{{ t('取消') }}</CButton>
        <CButton color="primary" type="submit" :disabled="!password">{{ t('確認') }}</CButton>
      </CModalFooter>
    </form>
  </CModal>
</template>
