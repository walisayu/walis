<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ApiError, api } from '../services/api'
import type { BackupProvider, BackupTarget } from '../types'

// The wizard for adding/editing a backup destination: walks the user step by step through getting things out of each provider's console.
// For the three OAuth providers the refresh token is the sticking point, so "build the authorization URL -> paste the code -> exchange the token" is folded in here;
// the user opens the authorization themselves (Walis never signs in on their behalf) and the backend exchanges the pasted code for a refresh token.
const props = defineProps<{ visible: boolean; target: BackupTarget | null }>()
const emit = defineEmits<{ (event: 'close'): void; (event: 'apply', target: BackupTarget): void }>()

const { t } = useI18n()

type StepKey = 'provider' | 'app' | 'authorize' | 'location' | 'verify'

interface ProviderInfo {
  label: string
  summary: string
  limit?: string
}

const providers: Record<BackupProvider, ProviderInfo> = {
  local: { label: '本機或外接磁碟', summary: '存到這台機器的目錄；掛載起來的 NAS 或外接磁碟也算。' },
  s3: { label: 'S3 相容（R2、AWS S3、MinIO）', summary: '用 Access Key 上傳到 bucket，不必註冊應用程式。' },
  dropbox: { label: 'Dropbox', summary: '要在 Dropbox 建立應用程式並授權一次。', limit: '單一檔案上限 150 MB。' },
  'google-drive': { label: 'Google Drive', summary: '要在 Google Cloud 建立 OAuth 用戶端並授權一次。' },
  onedrive: { label: 'OneDrive', summary: '要在 Azure 註冊應用程式並授權一次。', limit: '單一檔案上限 250 MB。' }
}

type S3Flavor = 'r2' | 'aws' | 'minio' | 'other'

const draft = ref<BackupTarget>(blank())
const step = ref<StepKey>('provider')
const isEdit = ref(false)
const s3Flavor = ref<S3Flavor>('r2')

const authorizeUrl = ref('')
const redirectUri = ref('')
const code = ref('')
const manualToken = ref(false)
const exchanging = ref(false)
const exchangeResult = ref<{ ok: boolean; message: string } | null>(null)

const testing = ref(false)
const testResult = ref<{ ok: boolean; message: string } | null>(null)

function blank(): BackupTarget {
  return {
    id: crypto.randomUUID(),
    name: '',
    provider: 'local',
    isDisabled: false,
    path: '',
    bucket: null,
    region: null,
    endpoint: null,
    accessKeyId: null,
    secretAccessKey: null,
    clientId: null,
    clientSecret: null,
    refreshToken: null
  }
}

watch(
  () => [props.visible, props.target] as const,
  ([visible, target]) => {
    if (!visible) return
    draft.value = target ? { ...target } : blank()
    isEdit.value = target !== null
    step.value = target ? 'app' : 'provider'
    authorizeUrl.value = ''
    code.value = ''
    manualToken.value = Boolean(target?.refreshToken)
    exchangeResult.value = null
    testResult.value = null
    s3Flavor.value = guessFlavor(target)
  },
  { immediate: true }
)

function guessFlavor(target: BackupTarget | null): S3Flavor {
  if (!target || target.provider !== 's3') return 'r2'
  if (!target.endpoint) return 'aws'
  if (target.endpoint.includes('r2.cloudflarestorage.com')) return 'r2'
  return 'other'
}

const usesOAuth = computed(() => ['dropbox', 'google-drive', 'onedrive'].includes(draft.value.provider))
const usesKeys = computed(() => draft.value.provider === 's3')

const steps = computed<{ key: StepKey; label: string }[]>(() => {
  const list: { key: StepKey; label: string }[] = [{ key: 'provider', label: '選擇類型' }]
  if (usesOAuth.value) {
    list.push({ key: 'app', label: '建立應用程式' }, { key: 'authorize', label: '授權' })
  } else if (usesKeys.value) {
    list.push({ key: 'app', label: '存取金鑰' })
  }
  list.push({ key: 'location', label: '位置與名稱' }, { key: 'verify', label: '測試並完成' })
  return list
})

const stepIndex = computed(() => Math.max(0, steps.value.findIndex((item) => item.key === step.value)))

const canNext = computed(() => {
  switch (step.value) {
    case 'app':
      if (usesOAuth.value) return Boolean(draft.value.clientId && draft.value.clientSecret)
      if (usesKeys.value) return Boolean(draft.value.bucket && draft.value.accessKeyId && draft.value.secretAccessKey && (s3Flavor.value === 'aws' ? draft.value.region : draft.value.endpoint))
      return true
    case 'authorize':
      return Boolean(draft.value.refreshToken)
    case 'location':
      return Boolean(draft.value.name.trim()) && (draft.value.provider === 's3' || Boolean(draft.value.path.trim()))
    default:
      return true
  }
})

function go(offset: 1 | -1): void {
  const next = steps.value[stepIndex.value + offset]
  if (!next) return
  step.value = next.key
  if (next.key === 'verify') {
    void runTest()
  }
}

function chooseProvider(provider: BackupProvider): void {
  if (draft.value.provider !== provider) {
    draft.value = { ...blank(), id: draft.value.id, name: draft.value.name, provider }
    authorizeUrl.value = ''
    exchangeResult.value = null
    if (provider === 's3') {
      applyFlavor('r2')
    }
  }
}

function applyFlavor(flavor: S3Flavor): void {
  s3Flavor.value = flavor
  if (flavor === 'r2') {
    draft.value.region = 'auto'
  } else if (flavor === 'aws') {
    draft.value.endpoint = null
    if (draft.value.region === 'auto') draft.value.region = ''
  } else if (flavor === 'minio' && draft.value.region === 'auto') {
    draft.value.region = 'us-east-1'
  }
}

async function buildAuthorizeUrl(): Promise<void> {
  exchangeResult.value = null
  try {
    const result = await api.backupOAuthUrl(draft.value.provider, draft.value.clientId ?? '')
    authorizeUrl.value = result.url
    redirectUri.value = result.redirectUri
  } catch (error) {
    exchangeResult.value = { ok: false, message: error instanceof ApiError ? error.message : String(error) }
  }
}

async function exchange(): Promise<void> {
  exchanging.value = true
  exchangeResult.value = null
  try {
    const result = await api.backupOAuthExchange({
      provider: draft.value.provider,
      clientId: draft.value.clientId,
      clientSecret: draft.value.clientSecret,
      code: extractCode(code.value),
      redirectUri: redirectUri.value || null,
      targetId: isEdit.value ? draft.value.id : null
    })
    exchangeResult.value = { ok: result.ok, message: result.message }
    if (result.ok && result.refreshToken) {
      draft.value.refreshToken = result.refreshToken
      code.value = ''
    }
  } catch (error) {
    exchangeResult.value = { ok: false, message: error instanceof ApiError ? error.message : String(error) }
  } finally {
    exchanging.value = false
  }
}

/** The user may paste the whole URL (http://localhost/?code=…); pull the code out of it. */
function extractCode(raw: string): string {
  const text = raw.trim()
  if (text.includes('code=')) {
    try {
      const url = new URL(text)
      return url.searchParams.get('code') ?? text
    } catch {
      const match = /code=([^&\s]+)/.exec(text)
      return match ? decodeURIComponent(match[1]!) : text
    }
  }
  return text
}

async function runTest(): Promise<void> {
  testing.value = true
  testResult.value = null
  try {
    testResult.value = await api.testBackupTarget(draft.value)
  } catch (error) {
    testResult.value = { ok: false, message: error instanceof Error ? error.message : String(error) }
  } finally {
    testing.value = false
  }
}

function finish(): void {
  emit('apply', { ...draft.value })
}

const copied = ref(false)
async function copyUrl(): Promise<void> {
  try {
    await navigator.clipboard.writeText(authorizeUrl.value)
    copied.value = true
    window.setTimeout(() => (copied.value = false), 2000)
  } catch {
    copied.value = false
  }
}

const pathLabel = computed(() => {
  switch (draft.value.provider) {
    case 'google-drive':
      return '資料夾 ID'
    case 's3':
      return '前綴（可留空）'
    case 'local':
      return '目錄的絕對路徑'
    default:
      return '資料夾路徑'
  }
})
</script>

<template>
  <CModal size="lg" scrollable :visible="visible" @close="emit('close')">
    <CModalHeader>
      <CModalTitle>{{ t(isEdit ? '編輯備份目的地' : '新增備份目的地') }}</CModalTitle>
    </CModalHeader>
    <CModalBody>
      <ol class="wizard-steps mb-4">
        <li
          v-for="(item, index) in steps"
          :key="item.key"
          :class="{ 'is-active': item.key === step, 'is-done': index < stepIndex }"
        >
          <span class="wizard-index">{{ index + 1 }}</span>{{ t(item.label) }}
        </li>
      </ol>

      <!-- 1. Choose the type -->
      <template v-if="step === 'provider'">
        <p class="text-body-secondary small">{{ t('選一種存放備份的地方；之後每一步只會出現那一種需要的設定。') }}</p>
        <div class="d-grid gap-2">
          <button
            v-for="(info, key) in providers"
            :key="key"
            type="button"
            class="wizard-choice text-start"
            :class="{ 'is-selected': draft.provider === key }"
            @click="chooseProvider(key as BackupProvider)"
          >
            <div class="fw-semibold">{{ t(info.label) }}</div>
            <div class="small text-body-secondary">
              {{ t(info.summary) }}
              <span v-if="info.limit" class="text-warning-emphasis"> {{ t(info.limit) }}</span>
            </div>
          </button>
        </div>
      </template>

      <!-- 2. Create the application (OAuth) / access key (S3) -->
      <template v-else-if="step === 'app' && draft.provider === 'dropbox'">
        <ol class="wizard-guide">
          <li>
            {{ t('開啟 Dropbox App Console，按「Create app」。') }}
            <a href="https://www.dropbox.com/developers/apps/create" target="_blank" rel="noopener noreferrer">dropbox.com/developers/apps</a>
          </li>
          <li>{{ t('API 選「Scoped access」，存取範圍選「App folder」（備份只會放在自己的資料夾）或「Full Dropbox」，取一個名字。') }}</li>
          <li>
            <strong>{{ t('先到 Permissions 分頁勾權限，再授權。') }}</strong>
            {{ t('需要 files.content.write、files.content.read 與 files.metadata.read。先授權再改權限的話，拿到的 token 沒有那些權限，得整個重來。') }}
          </li>
          <li>{{ t('回到 Settings 分頁，把 App key 與 App secret 填到下面。') }}</li>
        </ol>
        <CRow class="g-3">
          <CCol :md="6">
            <CFormLabel>{{ t('App key（Client ID）') }}</CFormLabel>
            <CFormInput v-model="draft.clientId" class="font-monospace" autocomplete="off" />
          </CCol>
          <CCol :md="6">
            <CFormLabel>{{ t('App secret（Client Secret）') }}</CFormLabel>
            <CFormInput v-model="draft.clientSecret" type="password" class="font-monospace" autocomplete="new-password" />
          </CCol>
        </CRow>
      </template>

      <template v-else-if="step === 'app' && draft.provider === 'google-drive'">
        <ol class="wizard-guide">
          <li>
            {{ t('開啟 Google Cloud Console 的「API 和服務 → 憑證」，選一個專案（沒有就建一個）。') }}
            <a href="https://console.cloud.google.com/apis/credentials" target="_blank" rel="noopener noreferrer">console.cloud.google.com/apis/credentials</a>
          </li>
          <li>{{ t('到「已啟用的 API」啟用 Google Drive API。') }}</li>
          <li>{{ t('第一次要先設定「OAuth 同意畫面」：使用者類型選外部，填名稱與信箱即可，並把你的 Google 帳號加進測試使用者。') }}</li>
          <li>{{ t('建立憑證 → OAuth 用戶端 ID，應用程式類型選「電腦版應用程式」。建好後複製用戶端 ID 與用戶端密碼。') }}</li>
          <li>
            <strong>{{ t('同意畫面停在「測試中」時，refresh token 七天就會失效。') }}</strong>
            {{ t('備份一週後開始失敗多半是這個；到同意畫面按「發布應用程式」改成正式狀態就不會有七天的限制。') }}
            {{ t('正式狀態的 token 閒置約六個月也會被撤銷：停用很久的目的地重新啟用時，要回來重新授權。') }}
          </li>
        </ol>
        <CRow class="g-3">
          <CCol :md="6">
            <CFormLabel>{{ t('用戶端 ID（Client ID）') }}</CFormLabel>
            <CFormInput v-model="draft.clientId" class="font-monospace" autocomplete="off" />
          </CCol>
          <CCol :md="6">
            <CFormLabel>{{ t('用戶端密碼（Client Secret）') }}</CFormLabel>
            <CFormInput v-model="draft.clientSecret" type="password" class="font-monospace" autocomplete="new-password" />
          </CCol>
        </CRow>
      </template>

      <template v-else-if="step === 'app' && draft.provider === 'onedrive'">
        <ol class="wizard-guide">
          <li>
            {{ t('開啟 Azure 入口網站的「應用程式註冊」，按「新增註冊」。') }}
            <a href="https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade" target="_blank" rel="noopener noreferrer">portal.azure.com</a>
          </li>
          <li>{{ t('支援的帳戶類型選「任何組織目錄中的帳戶和個人 Microsoft 帳戶」；重新導向 URI 選「Web」並填 http://localhost。') }}</li>
          <li>{{ t('註冊後在「概觀」複製「應用程式（用戶端）識別碼」。') }}</li>
          <li>
            {{ t('到「憑證及祕密」新增用戶端密碼，馬上複製「值」（離開頁面就看不到了）。') }}
            <strong>{{ t('密碼有到期日，到期那天備份就會開始失敗，到期前要換一把再回來重新授權。') }}</strong>
          </li>
          <li>{{ t('「API 權限」要有 Microsoft Graph 的 Files.ReadWrite 與 offline_access（委派）。') }}</li>
        </ol>
        <CRow class="g-3">
          <CCol :md="6">
            <CFormLabel>{{ t('應用程式識別碼（Client ID）') }}</CFormLabel>
            <CFormInput v-model="draft.clientId" class="font-monospace" autocomplete="off" />
          </CCol>
          <CCol :md="6">
            <CFormLabel>{{ t('用戶端密碼的值（Client Secret）') }}</CFormLabel>
            <CFormInput v-model="draft.clientSecret" type="password" class="font-monospace" autocomplete="new-password" />
          </CCol>
        </CRow>
      </template>

      <template v-else-if="step === 'app' && draft.provider === 's3'">
        <p class="text-body-secondary small">{{ t('三種服務的差別只在 endpoint 與區域，先選你用的是哪一家。') }}</p>
        <CButtonGroup class="mb-3" role="group">
          <CButton v-for="flavor in (['r2', 'aws', 'minio', 'other'] as S3Flavor[])" :key="flavor" size="sm" :color="s3Flavor === flavor ? 'primary' : 'secondary'" :variant="s3Flavor === flavor ? undefined : 'outline'" @click="applyFlavor(flavor)">
            {{ { r2: 'Cloudflare R2', aws: 'AWS S3', minio: 'MinIO', other: t('其他') }[flavor] }}
          </CButton>
        </CButtonGroup>
        <ol v-if="s3Flavor === 'r2'" class="wizard-guide">
          <li>{{ t('在 Cloudflare 的 R2 建一個 bucket。') }}</li>
          <li>{{ t('到 R2 → 「管理 R2 API 權杖」建立權杖，權限選「物件讀取與寫入」並限定這個 bucket；複製 Access Key ID 與 Secret Access Key。') }}</li>
          <li>{{ t('endpoint 是 https://<帳號 ID>.r2.cloudflarestorage.com（帳號 ID 在 R2 總覽右側），區域固定填 auto。') }}</li>
        </ol>
        <ol v-else-if="s3Flavor === 'aws'" class="wizard-guide">
          <li>{{ t('在 S3 建一個 bucket，記下它的區域（例如 ap-northeast-1）。') }}</li>
          <li>{{ t('到 IAM 建一個使用者，只給這個 bucket 的 s3:PutObject、s3:GetObject、s3:DeleteObject 與 s3:ListBucket，建立存取金鑰。') }}</li>
          <li>{{ t('endpoint 留空，Walis 會依區域組出網址。') }}</li>
        </ol>
        <ol v-else class="wizard-guide">
          <li>{{ t('建一個 bucket，並準備一組能讀寫它的 Access Key。') }}</li>
          <li>{{ t('endpoint 填服務的網址（例如 https://minio.example.com:9000）；區域隨服務要求，MinIO 通常是 us-east-1。') }}</li>
        </ol>
        <CRow class="g-3">
          <CCol :md="6">
            <CFormLabel>{{ t('Bucket') }}</CFormLabel>
            <CFormInput v-model="draft.bucket" class="font-monospace" />
          </CCol>
          <CCol :md="6">
            <CFormLabel>{{ t('區域') }}</CFormLabel>
            <CFormInput v-model="draft.region" class="font-monospace" :placeholder="s3Flavor === 'r2' ? 'auto' : 'ap-northeast-1'" />
          </CCol>
          <CCol v-if="s3Flavor !== 'aws'" :xs="12">
            <CFormLabel>{{ t('Endpoint') }}</CFormLabel>
            <CFormInput v-model="draft.endpoint" class="font-monospace" placeholder="https://<帳號 ID>.r2.cloudflarestorage.com" />
          </CCol>
          <CCol :md="6">
            <CFormLabel>{{ t('Access Key ID') }}</CFormLabel>
            <CFormInput v-model="draft.accessKeyId" class="font-monospace" autocomplete="off" />
          </CCol>
          <CCol :md="6">
            <CFormLabel>{{ t('Secret Access Key') }}</CFormLabel>
            <CFormInput v-model="draft.secretAccessKey" type="password" class="font-monospace" autocomplete="new-password" />
          </CCol>
        </CRow>
      </template>

      <!-- 3. Authorize (OAuth) -->
      <template v-else-if="step === 'authorize'">
        <template v-if="!manualToken">
          <ol class="wizard-guide">
            <li>
              {{ t('按下面的按鈕產生授權網址，開新分頁登入並同意。') }}
              <span v-if="draft.provider === 'dropbox'">{{ t('Dropbox 會把授權碼直接顯示在頁面上。') }}</span>
              <span v-else>{{ t('同意後瀏覽器會跳到 http://localhost 而顯示「無法連線」，這是正常的：把網址列整段複製下來，授權碼就在裡面。') }}</span>
            </li>
            <li>{{ t('把授權碼（或整段網址）貼到下面，按「換取 refresh token」。授權碼只能用一次、幾分鐘內就會失效，貼錯了重新授權一次即可。') }}</li>
          </ol>

          <div class="d-flex flex-wrap gap-2 mb-3">
            <CButton color="primary" size="sm" :disabled="!draft.clientId" @click="buildAuthorizeUrl">{{ t('產生授權網址') }}</CButton>
            <template v-if="authorizeUrl">
              <CButton color="secondary" variant="outline" size="sm" component="a" :href="authorizeUrl" target="_blank" rel="noopener noreferrer">
                {{ t('開啟授權頁面') }}
              </CButton>
              <CButton color="secondary" variant="outline" size="sm" @click="copyUrl">{{ t(copied ? '已複製' : '複製網址') }}</CButton>
            </template>
          </div>
          <CFormInput v-if="authorizeUrl" :model-value="authorizeUrl" readonly class="font-monospace small mb-3" />

          <CFormLabel>{{ t('授權碼') }}</CFormLabel>
          <div class="d-flex gap-2">
            <CFormInput v-model="code" class="font-monospace" :placeholder="t('貼上授權碼或整段網址')" autocomplete="off" />
            <CButton color="primary" class="text-nowrap" :disabled="exchanging || !code.trim() || !draft.clientSecret" @click="exchange">
              <CSpinner v-if="exchanging" size="sm" class="me-1" />{{ t('換取 refresh token') }}
            </CButton>
          </div>
          <CAlert v-if="exchangeResult" :color="exchangeResult.ok ? 'success' : 'danger'" class="mt-3 mb-0 py-2 small">
            {{ exchangeResult.message }}
          </CAlert>
          <CAlert v-else-if="draft.refreshToken" color="success" class="mt-3 mb-0 py-2 small">
            {{ t('已經有 refresh token，可以直接下一步；要換新的就重新授權。') }}
          </CAlert>
          <CButton color="link" size="sm" class="px-0 mt-2" @click="manualToken = true">{{ t('我已經有 refresh token，直接貼') }}</CButton>
        </template>

        <template v-else>
          <CFormLabel>{{ t('Refresh Token') }}</CFormLabel>
          <CFormInput v-model="draft.refreshToken" type="password" class="font-monospace" autocomplete="new-password" />
          <CFormText>{{ t('編輯既有目的地時這裡是遮蔽字串，原樣留著代表沿用。') }}</CFormText>
          <CButton color="link" size="sm" class="px-0 mt-2" @click="manualToken = false">{{ t('改用授權流程取得') }}</CButton>
        </template>
      </template>

      <!-- 4. Location and name -->
      <template v-else-if="step === 'location'">
        <CRow class="g-3">
          <CCol :xs="12">
            <CFormLabel>{{ t('名稱') }}</CFormLabel>
            <CFormInput v-model="draft.name" :placeholder="t('例如：R2 異地備份')" />
          </CCol>
          <CCol :xs="12">
            <CFormLabel>{{ t(pathLabel) }}</CFormLabel>
            <CFormInput v-model="draft.path" class="font-monospace" :placeholder="draft.provider === 'local' ? '/Volumes/backup/walis' : draft.provider === 'google-drive' ? '1AbCdEfGh…' : draft.provider === 's3' ? 'walis/' : '/walis-backups'" />
            <CFormText v-if="draft.provider === 'google-drive'">
              {{ t('在 Google Drive 開啟要放備份的資料夾，網址最後那一段（folders/ 後面）就是資料夾 ID；不是資料夾名稱。') }}
            </CFormText>
            <CFormText v-else-if="draft.provider === 'local'">{{ t('目錄不存在會自動建立；請用絕對路徑。') }}</CFormText>
            <CFormText v-else-if="draft.provider === 's3'">{{ t('Bucket 底下的前綴，可以留空。') }}</CFormText>
            <CFormText v-else>{{ t('相對於帳號根目錄（App folder 模式則相對於應用程式資料夾），不存在會自動建立。') }}</CFormText>
          </CCol>
          <CCol :xs="12">
            <CFormSwitch :model-value="!draft.isDisabled" :label="t('啟用這個目的地')" @update:model-value="(value: boolean) => (draft.isDisabled = !value)" />
          </CCol>
        </CRow>
      </template>

      <!-- 5. Test and finish -->
      <template v-else-if="step === 'verify'">
        <div class="d-flex align-items-center gap-2 mb-3">
          <CButton color="secondary" variant="outline" size="sm" :disabled="testing" @click="runTest">
            <CSpinner v-if="testing" size="sm" class="me-1" />{{ t('再測一次') }}
          </CButton>
          <span v-if="testing" class="small text-body-secondary">{{ t('正在連線…') }}</span>
        </div>
        <CAlert v-if="testResult" :color="testResult.ok ? 'success' : 'danger'" class="py-2">
          {{ testResult.message }}
        </CAlert>
        <p class="small text-body-secondary mb-0">
          {{ t('按「完成」只是把目的地放進設定表單，回到頁面後還要按「儲存設定」才會生效。') }}
          <template v-if="testResult && !testResult.ok">{{ t('測試失敗也可以先完成，之後再回來改。') }}</template>
        </p>
      </template>
    </CModalBody>
    <CModalFooter>
      <CButton color="secondary" variant="ghost" @click="emit('close')">{{ t('取消') }}</CButton>
      <CButton v-if="stepIndex > 0" color="secondary" variant="outline" @click="go(-1)">{{ t('上一步') }}</CButton>
      <CButton v-if="step !== 'verify'" color="primary" :disabled="!canNext" @click="go(1)">{{ t('下一步') }}</CButton>
      <CButton v-else color="primary" :disabled="testing" @click="finish">{{ t('完成') }}</CButton>
    </CModalFooter>
  </CModal>
</template>

<style scoped>
.wizard-steps {
  display: flex;
  flex-wrap: wrap;
  gap: .5rem 1.25rem;
  padding: 0;
  list-style: none;
  font-size: .875rem;
  color: var(--cui-secondary-color);
}

.wizard-steps li {
  display: flex;
  align-items: center;
  gap: .4rem;
}

.wizard-steps .is-active {
  color: var(--cui-body-color);
  font-weight: 600;
}

.wizard-index {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 1.5rem;
  height: 1.5rem;
  border-radius: 50%;
  border: 1px solid var(--cui-border-color);
  font-size: .75rem;
}

.wizard-steps .is-active .wizard-index {
  border-color: var(--cui-primary);
  background: var(--cui-primary);
  color: #fff;
}

.wizard-steps .is-done .wizard-index {
  border-color: var(--cui-success);
  color: var(--cui-success);
}

.wizard-choice {
  padding: .75rem 1rem;
  border: 1px solid var(--cui-border-color);
  border-radius: var(--cui-border-radius);
  background: var(--cui-body-bg);
}

.wizard-choice:hover {
  border-color: var(--cui-primary);
}

.wizard-choice.is-selected {
  border-color: var(--cui-primary);
  box-shadow: 0 0 0 .15rem rgba(var(--cui-primary-rgb), .2);
}

.wizard-guide {
  padding-left: 1.25rem;
  margin-bottom: 1rem;
}

.wizard-guide li {
  margin-bottom: .5rem;
}
</style>
