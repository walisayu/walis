<script setup lang="ts">
import { onBeforeUnmount, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import OtpEnrollmentFields from './OtpEnrollmentFields.vue'
import { useOtpEnrollment } from '../composables/useOtpEnrollment'

const props = defineProps<{ visible: boolean }>()
const emit = defineEmits<{ (event: 'close'): void }>()

const { t } = useI18n()
const otp = useOtpEnrollment()

function close(): void {
  otp.reset()
  emit('close')
}

watch(
  () => props.visible,
  (visible) => {
    // The secret is only requested once the password has been entered; OTP seeds, the password and one-time
    // recovery codes must not remain in a hidden modal.
    if (!visible) {
      otp.reset()
    }
  }
)

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
</script>

<template>
  <CModal :visible="visible" @close="close">
    <CModalHeader>
      <CModalTitle>{{ t('設定一次性密碼') }}</CModalTitle>
    </CModalHeader>

    <CModalBody>
      <OtpEnrollmentFields
        v-model="otp.code.value"
        v-model:current-password="otp.currentPassword.value"
        :enrollment="otp.enrollment.value"
        :qr-image-url="otp.qrImageUrl.value"
        :recovery-codes="otp.recoveryCodes.value"
        :busy="otp.busy.value"
        :error="otp.error.value"
        @submit="proceed"
      />
    </CModalBody>

    <CModalFooter>
      <CButton v-if="otp.recoveryCodes.value.length" color="secondary" variant="outline" @click="otp.copyCodes">
        <CIcon icon="cil-copy" class="me-1" />{{ t('複製備援碼') }}
      </CButton>
      <CButton v-if="otp.recoveryCodes.value.length" color="primary" @click="close">{{ t('完成') }}</CButton>

      <template v-else>
        <CButton color="secondary" variant="outline" @click="close">{{ t('取消') }}</CButton>
        <CButton
          color="primary"
          :disabled="otp.busy.value || (otp.enrollment.value ? !otp.code.value : !otp.currentPassword.value)"
          @click="proceed"
        >
          <CSpinner v-if="otp.busy.value" size="sm" class="me-2" />{{ otp.enrollment.value ? t('啟用') : t('繼續') }}
        </CButton>
      </template>
    </CModalFooter>
  </CModal>
</template>
