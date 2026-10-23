import { ref } from 'vue'
import QRCode from 'qrcode'
import { useI18n } from 'vue-i18n'
import { ApiError, api } from '../services/api'
import { useToastStore } from '../stores/toasts'
import type { OtpEnrollment } from '../types'

/**
 * The one-time-password enrollment flow: ask the backend for a secret, draw it as a QR code, and receive backup codes after verification.
 * Voluntary setup (the modal in account management) and forced setup (the full page after sign-in) share the same logic;
 * only the frame and buttons differ.
 */
export function useOtpEnrollment() {
  const { t } = useI18n()
  const toasts = useToastStore()

  const enrollment = ref<OtpEnrollment | null>(null)
  const qrImageUrl = ref('')
  const code = ref('')
  // Starting and enabling both re-verify the account password on the backend; it is asked once and reused for the enable call.
  const currentPassword = ref('')
  const recoveryCodes = ref<string[]>([])
  const busy = ref(false)
  const error = ref<string | null>(null)
  let operation = 0

  function reset(): void {
    operation += 1
    enrollment.value = null
    qrImageUrl.value = ''
    code.value = ''
    currentPassword.value = ''
    recoveryCodes.value = []
    busy.value = false
    error.value = null
  }

  async function start(): Promise<void> {
    const password = currentPassword.value
    reset()
    currentPassword.value = password
    const currentOperation = operation
    busy.value = true

    try {
      const nextEnrollment = await api.startOtpEnrollment(password)

      if (currentOperation !== operation) {
        return
      }

      // Render to an image URL instead of injecting generated SVG markup into
      // the DOM. The otpauth URI remains data, never executable HTML.
      const nextQrImageUrl = await QRCode.toDataURL(nextEnrollment.uri, {
        type: 'image/png',
        margin: 1,
        width: 200
      })

      if (currentOperation !== operation) {
        return
      }

      enrollment.value = nextEnrollment
      qrImageUrl.value = nextQrImageUrl
    } catch (exception) {
      if (currentOperation === operation) {
        error.value = exception instanceof ApiError ? exception.message : String(exception)
      }
    } finally {
      if (currentOperation === operation) {
        busy.value = false
      }
    }
  }

  async function enable(): Promise<boolean> {
    const currentOperation = operation
    busy.value = true
    error.value = null

    try {
      const result = await api.enableOtp(code.value, currentPassword.value)

      if (currentOperation !== operation) {
        return false
      }

      recoveryCodes.value = result.recoveryCodes
      // Do not refresh auth here: in the mandatory enrollment screen that
      // would remove this component before the one-time recovery codes can be
      // saved. The parent refreshes only after the user explicitly finishes.
      toasts.success(t('已啟用一次性密碼'))
      return true
    } catch (exception) {
      if (currentOperation === operation) {
        error.value = exception instanceof ApiError ? exception.message : String(exception)
      }

      return false
    } finally {
      if (currentOperation === operation) {
        busy.value = false
      }
    }
  }

  async function copyCodes(): Promise<void> {
    try {
      await navigator.clipboard.writeText(recoveryCodes.value.join('\n'))
      toasts.success(t('已複製備援碼'))
    } catch {
      toasts.error(t('瀏覽器不允許存取剪貼簿。'))
    }
  }

  return { enrollment, qrImageUrl, code, currentPassword, recoveryCodes, busy, error, reset, start, enable, copyCodes }
}
