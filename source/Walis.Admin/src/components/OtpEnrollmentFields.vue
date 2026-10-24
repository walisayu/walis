<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import type { OtpEnrollment } from '../types'

const props = defineProps<{
  modelValue: string
  /** The account password; the backend re-verifies it when enrollment starts and again when it is enabled. */
  currentPassword: string
  enrollment: OtpEnrollment | null
  qrImageUrl: string
  recoveryCodes: string[]
  busy: boolean
  error: string | null
  /** The input ids; they must differ when two appear on the same page. */
  inputId?: string
}>()

const emit = defineEmits<{
  (event: 'update:modelValue', value: string): void
  (event: 'update:currentPassword', value: string): void
  (event: 'submit'): void
}>()

const { t } = useI18n()

const code = computed({
  get: () => props.modelValue,
  set: (value: string) => emit('update:modelValue', value)
})

const password = computed({
  get: () => props.currentPassword,
  set: (value: string) => emit('update:currentPassword', value)
})

const fieldId = computed(() => props.inputId ?? 'otp-code')
const passwordFieldId = computed(() => `${fieldId.value}-password`)
</script>

<template>
  <CAlert v-if="error" color="danger" class="py-2">{{ error }}</CAlert>

  <template v-if="!enrollment && !recoveryCodes.length && !busy">
    <p class="text-body-secondary">
      {{ t('為了確認是你本人，開始設定與啟用一次性密碼都要輸入目前的密碼。') }}
    </p>
    <CFormLabel :for="passwordFieldId">{{ t('目前的密碼') }}</CFormLabel>
    <CFormInput
      :id="passwordFieldId"
      v-model="password"
      type="password"
      autocomplete="current-password"
      @keyup.enter="emit('submit')"
    />
  </template>

  <template v-if="recoveryCodes.length">
    <CAlert color="success" class="py-2">{{ t('已啟用一次性密碼') }}</CAlert>
    <p class="text-body-secondary">
      {{ t('請把下列備援碼保存在安全的地方；每組只能使用一次，遺失驗證器時可以用來登入。') }}
    </p>
    <pre class="bg-body-tertiary border rounded p-3 mb-0"><code>{{ recoveryCodes.join('\n') }}</code></pre>
  </template>

  <template v-else-if="enrollment">
    <p class="text-body-secondary">
      {{ t('用驗證器掃描下方 QR Code，或手動輸入密鑰，再填入產生的 6 位數驗證碼。') }}
    </p>

    <div class="d-flex flex-wrap align-items-center gap-4 mb-3">
      <!-- The QR code is drawn as a PNG from the otpauth URI given by the backend; no dynamic SVG/HTML is injected into the DOM. -->
      <div class="bg-white p-2 rounded border" style="width: 218px">
        <img :src="qrImageUrl" alt="" width="200" height="200" class="d-block" />
      </div>
      <div>
        <div class="small text-body-secondary">{{ t('密鑰') }}</div>
        <code class="user-select-all text-break">{{ enrollment.secret }}</code>
        <div class="small text-body-secondary mt-2">
          {{ t('{digits} 位數 · 每 {period} 秒更新', { digits: enrollment.digits, period: enrollment.period }) }}
        </div>
      </div>
    </div>

    <CFormLabel :for="fieldId">{{ t('驗證碼') }}</CFormLabel>
    <CFormInput
      :id="fieldId"
      v-model="code"
      class="font-monospace"
      autocomplete="one-time-code"
      inputmode="text"
      placeholder="123456"
    />
  </template>

  <div v-else-if="busy" class="d-flex justify-content-center py-4">
    <CSpinner color="primary" />
  </div>
</template>
