<script setup lang="ts">
import { onBeforeUnmount } from 'vue'
import { useI18n } from 'vue-i18n'
import OtpEnrollmentFields from '../components/OtpEnrollmentFields.vue'
import { useOtpEnrollment } from '../composables/useOtpEnrollment'
import { useAuthStore } from '../stores/auth'

const emit = defineEmits<{ (event: 'done'): void }>()

const { t } = useI18n()
const auth = useAuthStore()
const otp = useOtpEnrollment()

// The secret is only requested once the password has been entered, so nothing starts on mount.
onBeforeUnmount(otp.reset)

async function proceed(): Promise<void> {
  if (otp.busy.value) {
    return
  }

  if (otp.enrollment.value) {
    await otp.enable()
  } else if (otp.currentPassword.value) {
    await otp.start()
  }
}

// Anyone who cannot finish the setup needs a way out, otherwise only clearing browser data leaves this page.
async function signOut(): Promise<void> {
  try {
    await auth.signOut()
  } catch (error) {
    otp.error.value = error instanceof Error ? error.message : String(error)
  }
}
</script>

<template>
  <div class="min-vh-100 d-flex align-items-center justify-content-center bg-body-tertiary px-3 py-4">
    <CCard class="shadow-sm" style="width: 100%; max-width: 34rem">
      <CCardBody class="p-4">
        <div class="d-flex align-items-center mb-4">
          <span
            class="d-inline-flex align-items-center justify-content-center bg-primary text-white rounded fw-bold me-3"
            style="width: 2.5rem; height: 2.5rem"
          >W</span>
          <div>
            <div class="fs-5 fw-semibold lh-sm">Walis</div>
            <div class="small text-body-secondary lh-sm">{{ t('CI/CD 管理中心') }}</div>
          </div>
        </div>

        <h1 class="h4 mb-1">{{ t('設定一次性密碼') }}</h1>
        <p class="text-body-secondary">
          {{ t('系統要求所有帳號都必須啟用一次性密碼，完成設定後才能使用管理介面。') }}
        </p>

        <OtpEnrollmentFields
          v-model="otp.code.value"
          v-model:current-password="otp.currentPassword.value"
          input-id="otp-required-code"
          :enrollment="otp.enrollment.value"
          :qr-image-url="otp.qrImageUrl.value"
          :recovery-codes="otp.recoveryCodes.value"
          :busy="otp.busy.value"
          :error="otp.error.value"
          @submit="proceed"
        />

        <template v-if="otp.recoveryCodes.value.length">
          <div class="d-flex gap-2 mt-4">
            <CButton color="secondary" variant="outline" @click="otp.copyCodes">
              <CIcon icon="cil-copy" class="me-1" />{{ t('複製備援碼') }}
            </CButton>
            <CButton color="primary" class="ms-auto" @click="emit('done')">{{ t('完成') }}</CButton>
          </div>
        </template>

        <template v-else>
          <CButton
            color="primary"
            class="w-100 mt-4"
            :disabled="otp.busy.value || (otp.enrollment.value ? !otp.code.value : !otp.currentPassword.value)"
            @click="proceed"
          >
            <CSpinner v-if="otp.busy.value" size="sm" class="me-2" />{{ otp.enrollment.value ? t('啟用') : t('繼續') }}
          </CButton>
          <CButton color="link" class="w-100 mt-2" @click="signOut">{{ t('登出') }}</CButton>
        </template>
      </CCardBody>
    </CCard>
  </div>
</template>
