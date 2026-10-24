<script setup lang="ts">
import { computed, nextTick, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ApiError } from '../services/api'
import { useAuthStore } from '../stores/auth'

const emit = defineEmits<{ (event: 'signed-in'): void }>()

const { t } = useI18n()
const auth = useAuthStore()

const form = reactive({
  username: '',
  displayName: '',
  password: '',
  bootstrapToken: '',
  code: '',
  // The server's current value: on by default on a loopback or non-production server, so the first administrator sees the
  // explanation below and either keeps it or turns it off before the first job exists.
  allowUnisolatedBuilds: true
})
const challenge = ref<string | null>(null)
const busy = ref(false)
const error = ref<string | null>(null)

const isSetup = computed(() => !auth.hasUsers)
const needsCode = computed(() => challenge.value !== null)

/** Shown only while builds may run without an isolation boundary; the answer travels with the setup request. */
const showsIsolationChoice = computed(() => isSetup.value && auth.unisolatedBuildsAllowed)

async function submit(): Promise<void> {
  busy.value = true
  error.value = null

  try {
    if (needsCode.value) {
      await auth.verifyOtp(challenge.value!, form.code)
      challenge.value = null
      form.code = ''
      emit('signed-in')
      return
    }

    if (isSetup.value) {
      await auth.setupAccount(
        form.username.trim(),
        form.displayName.trim() || form.username.trim(),
        form.password,
        form.bootstrapToken,
        showsIsolationChoice.value ? form.allowUnisolatedBuilds : null
      )
      form.password = ''
      form.bootstrapToken = ''
      emit('signed-in')
      return
    }

    const pending = await auth.signIn(form.username.trim(), form.password)

    if (pending) {
      challenge.value = pending
      // The password has already been accepted and is no longer needed for the
      // OTP step, so do not retain it in the hidden form.
      form.password = ''
      form.code = ''
      await nextTick()
      // CFormInput is a component, and without floatingLabel its root is a fragment,
      // so neither the template ref nor $el reaches the <input>; find it by id directly.
      document.getElementById('sign-in-code')?.focus()
      return
    }

    form.password = ''
    emit('signed-in')
  } catch (exception) {
    error.value = exception instanceof ApiError ? exception.message : String(exception)

    // When the code is wrong the backend issues a new challenge code so the user can retry directly.
    if (exception instanceof ApiError && exception.challenge) {
      challenge.value = exception.challenge
    }
  } finally {
    busy.value = false
  }
}

function backToPassword(): void {
  challenge.value = null
  form.password = ''
  form.code = ''
  error.value = null
}
</script>

<template>
  <div class="min-vh-100 d-flex align-items-center justify-content-center bg-body-tertiary px-3">
    <CCard class="shadow-sm" style="width: 100%; max-width: 26rem">
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

        <h1 class="h5 mb-1">
          {{ t(needsCode ? '兩階段驗證' : isSetup ? '建立管理帳號' : '登入') }}
        </h1>
        <p class="text-body-secondary small">
          {{
            t(
              needsCode
                ? '請輸入驗證器上的 6 位數驗證碼，或使用備援碼。'
                : isSetup
                  ? '這是第一次使用，請建立一個管理帳號。'
                  : '請輸入帳號與密碼以繼續。'
            )
          }}
        </p>

        <CAlert v-if="error" color="danger" class="py-2">{{ error }}</CAlert>

        <CForm novalidate @submit.prevent="submit">
          <template v-if="needsCode">
            <div class="mb-4">
              <CFormLabel for="sign-in-code">{{ t('驗證碼') }}</CFormLabel>
              <CFormInput
                id="sign-in-code"
                v-model="form.code"
                class="font-monospace"
                autocomplete="one-time-code"
                inputmode="text"
                placeholder="123456"
                autofocus
              />
            </div>

            <CButton color="primary" type="submit" class="w-100" :disabled="busy">
              <CSpinner v-if="busy" size="sm" class="me-2" />{{ t('驗證') }}
            </CButton>
            <CButton color="link" class="w-100 mt-2" :disabled="busy" @click="backToPassword">
              {{ t('改用其他帳號登入') }}
            </CButton>
          </template>

          <template v-else>
            <div class="mb-3">
              <CFormLabel for="sign-in-username">{{ t('帳號') }}</CFormLabel>
              <CFormInput id="sign-in-username" v-model="form.username" autocomplete="username" autofocus />
            </div>

            <div v-if="isSetup" class="mb-3">
              <CFormLabel for="sign-in-display">{{ t('顯示名稱') }}</CFormLabel>
              <CFormInput id="sign-in-display" v-model="form.displayName" />
            </div>

            <div class="mb-4">
              <CFormLabel for="sign-in-password">{{ t('密碼') }}</CFormLabel>
              <CFormInput
                id="sign-in-password"
                v-model="form.password"
                type="password"
                :autocomplete="isSetup ? 'new-password' : 'current-password'"
              />
              <CFormText v-if="isSetup">{{ t('密碼至少要 12 個字元。') }}</CFormText>
            </div>

            <div v-if="isSetup && auth.setupCodeRequired" class="mb-4">
              <CFormLabel for="sign-in-bootstrap">{{ t('設定碼') }}</CFormLabel>
              <CFormInput
                id="sign-in-bootstrap"
                v-model="form.bootstrapToken"
                class="font-monospace"
                autocomplete="off"
                autocapitalize="characters"
                spellcheck="false"
                placeholder="XXXXX-XXXXX-XXXXX-XXXXX"
              />
              <CFormText>
                {{ t('從其他機器進行第一次設定時需要。設定碼在伺服器啟動時印在主控台，也存在資料目錄的 setup-code.txt；部署時設定了 Security:BootstrapToken 的話，請輸入該值。') }}
              </CFormText>
            </div>

            <CAlert v-if="showsIsolationChoice" color="warning" class="py-2 small mb-4">
              <div class="fw-semibold mb-1">{{ t('建置隔離') }}</div>
              <div class="mb-2">
                {{ t('沒有隔離邊界時，任何能編輯或執行任務的人（operator）都讀得到這台伺服器與執行器上的全部機密：資料庫、密碼雜湊、環境變數與雲端金鑰。只有 macOS 內建執行器有沙箱；Linux、Windows 與所有遠端執行器預設都沒有。') }}
              </div>
              <CFormSwitch
                id="sign-in-allow-unisolated"
                v-model="form.allowUnisolatedBuilds"
                :label="t('我了解風險，允許在沒有隔離邊界時執行建置')"
              />
              <div class="text-body-secondary mt-1">
                {{ t('關閉後，沒有沙箱的執行器不會執行建置，直到你在「系統設定 → 建置」改變這個決定。') }}
              </div>
            </CAlert>

            <CButton color="primary" type="submit" class="w-100" :disabled="busy">
              <CSpinner v-if="busy" size="sm" class="me-2" />
              {{ t(isSetup ? '建立並登入' : '登入') }}
            </CButton>
          </template>
        </CForm>
      </CCardBody>
    </CCard>
  </div>
</template>
