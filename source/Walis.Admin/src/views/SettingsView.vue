<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import { useI18n } from 'vue-i18n'
import TablePagination from '../components/TablePagination.vue'
import { DEFAULT_PAGE_SIZE, PAGE_SIZES, usePagedRows } from '../composables/usePagedRows'
import BackupTargetWizard from '../components/BackupTargetWizard.vue'
import PageActions from '../components/PageActions.vue'
import { ApiError, api } from '../services/api'
import { setLocale } from '../i18n'
import { useFormat } from '../composables/useFormat'
import { useAuthStore } from '../stores/auth'
import { useLocalizationStore } from '../stores/localization'
import { useSettingsStore } from '../stores/settings'
import { useToastStore } from '../stores/toasts'
import type {
  AuditEntry,
  AuditFilter,
  BackupFile,
  BackupProvider,
  BackupRun,
  BackupTarget,
  SecretsStatus,
  WalisSettings
} from '../types'

const { t, locale } = useI18n()
const route = useRoute()
const settings = useSettingsStore()
const localization = useLocalizationStore()
const auth = useAuthStore()
const toasts = useToastStore()
const { dateTime } = useFormat()

const form = ref<WalisSettings | null>(null)
const errors = ref<Record<string, string[]>>({})
const saving = ref(false)
const activeTab = ref<'general' | 'build' | 'backup' | 'security' | 'audit'>('general')

/** The switch reads "refuse without a boundary", which is the inverse of the stored allowUnisolatedBuilds. */
const requireIsolation = computed({
  get: () => !(form.value?.allowUnisolatedBuilds ?? true),
  set: (value: boolean) => {
    if (form.value) {
      form.value.allowUnisolatedBuilds = !value
    }
  }
})

/** What the two switches mean for builds on this server's own runner; null when they simply run sandboxed. */
const localIsolationNotice = computed<string | null>(() => {
  if (!form.value) {
    return null
  }

  const sandboxed = settings.sandboxAvailable && form.value.isolateBuilds

  if (sandboxed) {
    return null
  }

  if (requireIsolation.value) {
    return '內建執行器在這台伺服器上沒有可套用的沙箱，不會執行建置；請把任務指派給下方清單中的遠端執行器。'
  }

  return settings.sandboxAvailable
    ? '沙箱已關閉：內建執行器的建置腳本會以 Walis 服務帳號的權限直接執行，讀得到 Walis 的資料目錄。'
    : '這台伺服器的平台沒有 Walis 能套用的沙箱：內建執行器的建置腳本會以 Walis 服務帳號的權限直接執行，讀得到 Walis 的資料目錄。請只讓可信任的人編輯任務。'
})

const audit = ref<AuditEntry[]>([])
const auditLoading = ref(false)
const auditTotal = ref(0)
const auditPageNumber = ref(1)
const auditPageSize = ref(DEFAULT_PAGE_SIZE)
const auditFilter = ref({ actor: '', entityType: '', entityId: '', from: '', to: '' })
const auditPages = computed(() => Math.max(1, Math.ceil(auditTotal.value / auditPageSize.value)))
const auditRangeFrom = computed(() => (auditTotal.value === 0 ? 0 : (auditPageNumber.value - 1) * auditPageSize.value + 1))
const auditRangeTo = computed(() => Math.min(auditTotal.value, auditPageNumber.value * auditPageSize.value))

/** A date-only input means the whole local day; "to" is exclusive, so the day after is sent. */
function auditRange(): { from?: string; to?: string } {
  const from = auditFilter.value.from ? new Date(`${auditFilter.value.from}T00:00:00`) : null
  const to = auditFilter.value.to ? new Date(`${auditFilter.value.to}T00:00:00`) : null
  if (to) to.setDate(to.getDate() + 1)
  return {
    from: from && !Number.isNaN(from.getTime()) ? from.toISOString() : undefined,
    to: to && !Number.isNaN(to.getTime()) ? to.toISOString() : undefined
  }
}

function currentAuditFilter(): AuditFilter {
  return {
    actor: auditFilter.value.actor.trim() || undefined,
    entityType: auditFilter.value.entityType.trim() || undefined,
    entityId: auditFilter.value.entityId.trim() || undefined,
    ...auditRange()
  }
}

const auditExportUrl = computed(() => api.auditExportUrl(currentAuditFilter()))

const outcomeLabels: Record<string, string> = {
  success: '成功',
  denied: '拒絕',
  failed: '失敗'
}

/** Secrets at rest: key source and the stored values the current key cannot read; admins only. */
const secretsStatus = ref<SecretsStatus | null>(null)
const resettingSecrets = ref(false)

async function loadSecrets(): Promise<void> {
  if (!auth.isAdmin) {
    return
  }

  try {
    secretsStatus.value = await api.settingsSecrets()
  } catch {
    secretsStatus.value = null
  }
}

async function resetUndecryptableSecrets(): Promise<void> {
  resettingSecrets.value = true

  try {
    const cleared = await api.resetUndecryptableSecrets()
    toasts.success(t('已清除 {count} 筆無法解密的機密，請重新填寫。', { count: cleared.count }))
    await Promise.all([loadSecrets(), settings.load(true)])
    form.value = settings.settings ? { ...settings.settings } : form.value
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  } finally {
    resettingSecrets.value = false
  }
}

const backupRuns = ref<BackupRun[]>([])
const backupRunning = ref(false)
const backupDefaultDirectory = ref('')
const backupPolling = ref<number | null>(null)
/** The destination being edited in the wizard; null means adding a new one. */
const targetEditor = ref<BackupTarget | null>(null)
const wizardVisible = ref(false)
const expandedRun = ref<string | null>(null)

const backupFiles = ref<BackupFile[]>([])
const backupDirectory = ref('')
const restoring = ref(false)
const restoreConfirm = ref(false)
const restoreForm = ref({ fileName: '', passphrase: '', includeArtifacts: true })

/** Each provider needs different fields; the form decides what to show from this table. */
const providerLabels: Record<BackupProvider, string> = {
  local: '本機或外接磁碟',
  s3: 'S3 相容（R2、AWS S3）',
  dropbox: 'Dropbox',
  'google-drive': 'Google Drive',
  onedrive: 'OneDrive'
}

const hasRemoteTarget = computed(
  () => form.value?.backupTargets.some((target) => !target.isDisabled && target.provider !== 'local') ?? false
)

const actionLabels: Record<string, string> = {
  create: '建立',
  update: '更新',
  delete: '刪除',
  run: '執行',
  cancel: '取消',
  download: '下載',
  export: '匯出',
  reveal: '顯示機密',
  'sign-in': '登入',
  'sign-out': '登出',
  'sign-in-failed': '登入失敗',
  'locked-out': '登入鎖定'
}

const entityLabels: Record<string, string> = {
  job: '任務',
  build: '建置',
  runner: '執行器',
  variable: '環境變數',
  settings: '系統設定',
  channel: '通知管道',
  translation: '翻譯',
  locale: '語系',
  user: '帳號',
  'api-key': 'API 金鑰',
  plugin: '外掛',
  action: '動作',
  'action-pack': '動作套件',
  backup: '備份',
  'ai-provider': 'AI 供應商'
}

const webhookUrl = computed(() => {
  const base = form.value?.baseUrl?.trim().replace(/\/$/, '') ?? ''
  return `${base}/api/webhooks/push`
})

function cloneSettings(value: WalisSettings): WalisSettings {
  return {
    ...value,
    // Never let edits (especially newly typed cloud credentials) mutate the
    // long-lived Pinia settings object before a successful, masked response.
    backupTargets: (value.backupTargets ?? []).map((target) => ({ ...target }))
  }
}

watch(
  () => settings.settings,
  (value) => {
    form.value = value ? cloneSettings(value) : null
  },
  { immediate: true }
)

watch(activeTab, async (tab) => {
  if (tab === 'audit') {
    await loadAudit()
  }

  if (tab === 'backup') {
    await loadBackup()
    await loadBackupFiles()
  }

  if (tab === 'security') {
    await auth.loadUsers().catch(() => undefined)
  }
})

let auditRequestId = 0

async function loadAudit(): Promise<void> {
  const id = ++auditRequestId
  auditLoading.value = true

  try {
    const response = await api.auditPage({
      ...currentAuditFilter(),
      page: auditPageNumber.value,
      pageSize: auditPageSize.value
    })

    // A slow old request arriving late must not overwrite a newer one.
    if (id !== auditRequestId) {
      return
    }

    audit.value = response.items
    auditTotal.value = response.total

    // Filtering may shrink the page count below the current page: fall back to the last page and fetch again.
    const last = Math.max(1, Math.ceil(response.total / response.pageSize))
    if (auditPageNumber.value > last) {
      auditPageNumber.value = last
    }
  } catch (error) {
    if (id === auditRequestId) {
      toasts.error(error instanceof Error ? error.message : String(error))
    }
  } finally {
    if (id === auditRequestId) {
      auditLoading.value = false
    }
  }
}

function applyAuditFilter(): void {
  auditPageNumber.value = 1
  void loadAudit()
}

function clearAuditFilter(): void {
  auditFilter.value = { actor: '', entityType: '', entityId: '', from: '', to: '' }
  applyAuditFilter()
}

watch(auditPageSize, () => {
  auditPageNumber.value = 1
  void loadAudit()
})

watch(auditPageNumber, () => {
  void loadAudit()
})

async function save(): Promise<void> {
  if (!form.value) {
    return
  }

  saving.value = true
  errors.value = {}

  try {
    await settings.save(form.value)
    toasts.success(t('系統設定已儲存'))
    await auth.refresh()

    if (form.value.locale !== locale.value) {
      setLocale(form.value.locale)
    }
  } catch (error) {
    if (error instanceof ApiError) {
      errors.value = error.errors
      toasts.error(error.message)
    } else {
      toasts.error(error instanceof Error ? error.message : String(error))
    }
  } finally {
    saving.value = false
  }
}

async function loadBackup(): Promise<void> {
  try {
    const payload = await api.backup()
    backupRuns.value = payload.runs
    backupRunning.value = payload.isRunning
    backupDefaultDirectory.value = payload.defaultDirectory

    // Keeps refreshing while the backup is running and stops polling automatically when it finishes.
    if (payload.isRunning && backupPolling.value === null) {
      backupPolling.value = window.setInterval(loadBackup, 3000)
    } else if (!payload.isRunning && backupPolling.value !== null) {
      window.clearInterval(backupPolling.value)
      backupPolling.value = null
    }
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  }
}

async function loadBackupFiles(): Promise<void> {
  try {
    const payload = await api.backupFiles()
    backupFiles.value = payload.files
    backupDirectory.value = payload.directory

    if (!restoreForm.value.fileName && payload.files.length > 0) {
      restoreForm.value.fileName = payload.files[0].name
    }
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  }
}

/** A restore clears every table and writes the backup content back, so it always goes through a confirmation dialog. */
async function restore(): Promise<void> {
  restoreConfirm.value = false
  restoring.value = true

  try {
    const result = await api.restoreBackup({
      fileName: restoreForm.value.fileName,
      passphrase: restoreForm.value.passphrase || undefined,
      includeArtifacts: restoreForm.value.includeArtifacts
    })

    toasts.info(t('已還原 {rows} 列資料到 {provider}。', { rows: result.rows, provider: result.provider }))
    restoreForm.value.passphrase = ''

    // The whole dataset was replaced, so settings and records must be fetched again.
    await settings.load(true)
    await loadBackup()
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  } finally {
    restoring.value = false
  }
}

async function runBackup(): Promise<void> {
  try {
    await api.runBackup()
    backupRunning.value = true
    toasts.info(t('已開始備份，完成後會出現在下方紀錄。'))
    await loadBackup()
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  }
}

function newTarget(): void {
  targetEditor.value = null
  wizardVisible.value = true
}

function editTarget(target: BackupTarget): void {
  targetEditor.value = { ...target }
  wizardVisible.value = true
}

/** The wizard's "Finish": puts it into the settings form; back on the page "Save settings" must still be clicked for it to take effect. */
function applyTarget(target: BackupTarget): void {
  if (!form.value) {
    return
  }

  const index = form.value.backupTargets.findIndex((item) => item.id === target.id)

  if (index >= 0) {
    form.value.backupTargets.splice(index, 1, target)
  } else {
    form.value.backupTargets.push(target)
  }

  wizardVisible.value = false
  targetEditor.value = null
}

function removeTarget(target: BackupTarget): void {
  if (!form.value) {
    return
  }

  form.value.backupTargets = form.value.backupTargets.filter((item) => item.id !== target.id)
}


function formatSize(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} B`
  }

  if (bytes < 1024 * 1024) {
    return `${(bytes / 1024).toFixed(1)} KB`
  }

  if (bytes < 1024 * 1024 * 1024) {
    return `${(bytes / 1024 / 1024).toFixed(1)} MB`
  }

  return `${(bytes / 1024 / 1024 / 1024).toFixed(2)} GB`
}

async function revert(): Promise<void> {
  await settings.load(true)
  errors.value = {}
  toasts.info(t('已還原為上次儲存的設定'))
}

async function copyWebhookUrl(): Promise<void> {
  try {
    await navigator.clipboard.writeText(webhookUrl.value)
    toasts.info(t('已複製推送 webhook 網址'))
  } catch {
    toasts.error(t('瀏覽器不允許存取剪貼簿。'))
  }
}

onMounted(async () => {
  const requestedTab = route.query.tab
  if (
    requestedTab === 'general' ||
    requestedTab === 'build' ||
    requestedTab === 'backup' ||
    requestedTab === 'security' ||
    requestedTab === 'audit'
  ) {
    activeTab.value = requestedTab
  }
  await Promise.all([settings.load(), localization.load()])
  await auth.loadUsers().catch(() => undefined)
  await loadSecrets()
})

onUnmounted(() => {
  if (backupPolling.value !== null) {
    window.clearInterval(backupPolling.value)
  }

  restoreForm.value.passphrase = ''
  targetEditor.value = null
  form.value = null
})

const backupTargetPage = usePagedRows(() => form.value?.backupTargets ?? [])
</script>

<template>
  <CCard v-if="form" class="mb-4">
    <CCardHeader class="p-0">
      <CNav variant="tabs" class="card-header-tabs mx-0 px-3 pt-3">
        <CNavItem>
          <button class="nav-link" type="button" :class="{ active: activeTab === 'general' }" @click="activeTab = 'general'">
            {{ t('一般') }}
          </button>
        </CNavItem>
        <CNavItem>
          <button class="nav-link" type="button" :class="{ active: activeTab === 'build' }" @click="activeTab = 'build'">
            {{ t('建置') }}
          </button>
        </CNavItem>
        <CNavItem>
          <button class="nav-link" type="button" :class="{ active: activeTab === 'backup' }" @click="activeTab = 'backup'">
            {{ t('備份') }}
          </button>
        </CNavItem>
        <CNavItem>
          <button class="nav-link" type="button" :class="{ active: activeTab === 'security' }" @click="activeTab = 'security'">
            {{ t('安全') }}
          </button>
        </CNavItem>
        <CNavItem>
          <button class="nav-link" type="button" :class="{ active: activeTab === 'audit' }" @click="activeTab = 'audit'">
            {{ t('稽核') }}
          </button>
        </CNavItem>
      </CNav>
    </CCardHeader>

    <CCardBody>
      <CRow v-if="activeTab === 'general'" class="g-3">
        <CCol :md="6">
          <CFormLabel for="site-name">{{ t('站台名稱') }}</CFormLabel>
          <CFormInput id="site-name" v-model="form.siteName" :invalid="Boolean(errors.siteName)" />
          <CFormFeedback invalid>{{ errors.siteName?.[0] }}</CFormFeedback>
        </CCol>
        <CCol :md="6">
          <CFormLabel for="base-url">{{ t('對外網址') }}</CFormLabel>
          <CFormInput id="base-url" v-model="form.baseUrl" :invalid="Boolean(errors.baseUrl)" />
          <CFormFeedback invalid>{{ errors.baseUrl?.[0] }}</CFormFeedback>
          <CFormText>{{ t('通知連結會使用此網址組成建置頁面位置。') }}</CFormText>
        </CCol>
        <CCol :md="6">
          <CFormLabel for="time-zone">{{ t('時區') }}</CFormLabel>
          <CFormSelect id="time-zone" v-model="form.timeZone">
            <option v-for="zone in settings.timeZones" :key="zone" :value="zone">{{ zone }}</option>
          </CFormSelect>
        </CCol>
        <CCol :md="6">
          <CFormLabel for="locale">{{ t('介面語系') }}</CFormLabel>
          <CFormSelect id="locale" v-model="form.locale">
            <option v-for="item in localization.locales" :key="item.code" :value="item.code">
              {{ item.nativeName }}
            </option>
          </CFormSelect>
          <CFormText>
            <RouterLink to="/localization">{{ t('語言與翻譯') }}</RouterLink>
          </CFormText>
        </CCol>
        <CCol :xs="12">
          <CFormLabel for="database-path">{{ t('資料庫位置') }}</CFormLabel>
          <CFormInput id="database-path" class="font-monospace" :model-value="settings.databasePath" readonly />
          <CFormText>{{ t('以 API content root 為基準；可用 ConnectionStrings:Walis 覆寫。') }}</CFormText>
        </CCol>
      </CRow>

      <CRow v-else-if="activeTab === 'build'" class="g-3">
        <CCol :md="4">
          <CFormLabel for="concurrency">{{ t('同時建置上限') }}</CFormLabel>
          <CFormInput id="concurrency" v-model.number="form.maxConcurrentBuilds" type="number" min="1" max="16" />
        </CCol>
        <CCol :md="4">
          <CFormLabel for="default-timeout">{{ t('預設逾時（分鐘）') }}</CFormLabel>
          <CFormInput id="default-timeout" v-model.number="form.defaultTimeoutMinutes" type="number" min="1" />
        </CCol>
        <CCol :md="4">
          <CFormLabel for="retention">{{ t('紀錄保留天數') }}</CFormLabel>
          <CFormInput id="retention" v-model.number="form.logRetentionDays" type="number" min="1" />
          <CFormText>{{ t('套用到建置紀錄與產出檔案；稽核紀錄另有自己的保留天數。') }}</CFormText>
        </CCol>
        <CCol :xs="12">
          <CFormLabel for="workspace">{{ t('預設工作目錄') }}</CFormLabel>
          <CFormInput id="workspace" v-model="form.defaultWorkingDirectory" class="font-monospace" />
        </CCol>
        <CCol :xs="12">
          <CFormLabel for="shell">{{ t('預設 Shell') }}</CFormLabel>
          <CFormInput id="shell" v-model="form.defaultShell" class="font-monospace" placeholder="/bin/zsh" />
        </CCol>
        <CCol :xs="12">
          <hr class="my-2" />
          <CFormSwitch id="isolate-builds" v-model="form.isolateBuilds" :label="t('隔離建置')" />
          <CFormText class="mb-2">
            {{ t('在有沙箱的平台（macOS）把每個步驟關進沙箱，擋住建置對 Walis 資料目錄的讀寫。') }}
          </CFormText>
          <!-- The margin goes on a wrapper: on the switch itself it lands on the input and drops it below its label. -->
          <div class="mt-3">
            <CFormSwitch
              id="require-isolation"
              v-model="requireIsolation"
              :label="t('沒有隔離邊界時拒絕執行')"
            />
          </div>
          <CFormText>
            {{ t('開啟後，沒有沙箱、也沒有列在下方清單的執行器不會執行建置。關閉時，這類執行器會以未隔離方式直接執行。') }}
          </CFormText>
          <CAlert v-if="localIsolationNotice" :color="requireIsolation ? 'info' : 'warning'" class="mt-2 mb-0 py-2 small">
            {{ t(localIsolationNotice) }}
          </CAlert>
          <!-- Shown until an administrator accepts; the server records who accepted and when. -->
          <CAlert
            v-if="form.allowUnisolatedBuilds && !form.unisolatedBuildsAcknowledged"
            color="danger"
            class="mt-2 mb-0 py-2 small"
          >
            <div class="mb-2">
              {{ t('沒有隔離邊界時，任何能編輯或執行任務的人（operator）都讀得到這台伺服器與執行器上的全部機密：資料庫、密碼雜湊、環境變數與雲端金鑰。只有 macOS 內建執行器有沙箱；Linux、Windows 與所有遠端執行器預設都沒有。') }}
            </div>
            <CFormSwitch
              id="acknowledge-unisolated"
              v-model="form.unisolatedBuildsAcknowledged"
              :label="t('我已了解，接受未隔離建置的風險')"
            />
          </CAlert>
          <CFormText v-else-if="form.allowUnisolatedBuilds && form.unisolatedBuildsAcknowledgedAt" class="d-block mt-2">
            {{
              t('未隔離建置的風險已由 {user} 於 {time} 接受。', {
                user: form.unisolatedBuildsAcknowledgedBy ?? '—',
                time: dateTime(form.unisolatedBuildsAcknowledgedAt)
              })
            }}
          </CFormText>
          <div v-if="settings.sandboxAvailable" class="mt-3">
            <CFormSwitch
              id="sandbox-deny-process-control"
              v-model="form.sandboxDenyProcessControl"
              :label="t('沙箱同時拒絕訊號、Mach 服務查詢與讀取其他程序的資訊')"
            />
            <CFormText>
              {{ t('預設開啟。擋住建置向服務或其他建置送訊號、向 launchd 與鑰匙圈查詢服務，以及用 ps -E 讀取其他程序的環境變數；腳本仍可結束自己啟動的子程序。') }}
            </CFormText>
          </div>
        </CCol>
        <CCol :xs="12">
          <CFormLabel for="external-runners">{{ t('已置於外部隔離的執行器') }}</CFormLabel>
          <CFormTextarea
            id="external-runners"
            v-model="form.externallyIsolatedRunners"
            class="font-monospace"
            rows="3"
          />
          <CFormText>
            {{ t('開啟「沒有隔離邊界時拒絕執行」時才會用到。一行一個執行器名稱，只列出整台都放在專用 VM 或容器裡的遠端執行器；執行器本身也要以 --external-isolation true 啟動，兩邊都宣告才會派工。') }}
          </CFormText>
        </CCol>
        <CCol :md="6">
          <CFormLabel for="denied-paths">{{ t('額外禁止存取的路徑') }}</CFormLabel>
          <CFormTextarea id="denied-paths" v-model="form.sandboxDeniedPaths" class="font-monospace" rows="3" />
          <CFormText>{{ t('一行一個絕對路徑；符號連結會自動解析成實際路徑。') }}</CFormText>
          <CFormText class="d-block">
            {{ t('沙箱一律拒絕 Walis 的資料目錄、安裝目錄（current、releases、var）與服務帳號的家目錄；任務自己的工作目錄不受影響。') }}
          </CFormText>
        </CCol>
        <CCol :md="6">
          <CFormLabel for="allowed-workdirs">{{ t('允許的工作目錄') }}</CFormLabel>
          <CFormTextarea id="allowed-workdirs" v-model="form.allowedWorkingDirectories" class="font-monospace" rows="3" />
          <CFormText>{{ t('一行一個；留空表示不限制。任務的工作目錄不在其中就直接失敗。') }}</CFormText>
        </CCol>
        <CCol :xs="12">
          <hr class="my-2" />
          <CFormSwitch id="keep-artifacts" v-model="form.keepArtifacts" :label="t('保留建置產出檔案')" />
          <CFormText class="mb-3">{{ t('建置成功後，依任務的產出檔案路徑保存，可在建置輸出視窗下載。') }}</CFormText>
          <CFormSwitch id="clean-workspace" v-model="form.cleanWorkspace" :label="t('每次建置前清空工作目錄')" />
          <CFormText class="mb-3">{{ t('只會刪除目錄內容；磁碟根目錄、家目錄與層級過淺的路徑一律拒絕。') }}</CFormText>
          <CFormSwitch id="clean-workspace-after" v-model="form.cleanWorkspaceAfterBuild" :label="t('每次建置後清空工作目錄')" />
          <CFormText>{{ t('建置結束後（不論成功或失敗）清空工作目錄，產出檔案與快取先保存；任務可以個別覆寫。') }}</CFormText>
        </CCol>
      </CRow>

      <template v-else-if="activeTab === 'backup'">
        <CRow class="g-3">
          <CCol :xs="12">
            <CFormSwitch id="backup-enabled" v-model="form.backupEnabled" :label="t('啟用排程備份')" />
            <CFormText>
              {{ t('備份內容：Walis 的資料庫與產出檔案、下方指定的路徑，以及選用的 PostgreSQL。') }}
            </CFormText>
          </CCol>

          <CCol :md="4">
            <CFormLabel for="backup-cron">{{ t('排程（Cron）') }}</CFormLabel>
            <CFormInput
              id="backup-cron"
              v-model="form.backupCron"
              class="font-monospace"
              placeholder="30 3 * * *"
              :invalid="Boolean(errors.backupCron)"
            />
            <CFormFeedback invalid>{{ errors.backupCron?.[0] }}</CFormFeedback>
            <CFormText>{{ t('五欄位：分 時 日 月 週，依系統設定的時區。') }}</CFormText>
          </CCol>

          <CCol :md="4">
            <CFormLabel for="backup-keep">{{ t('保留天數') }}</CFormLabel>
            <CFormInput
              id="backup-keep"
              v-model.number="form.backupKeepDays"
              type="number"
              min="1"
              :invalid="Boolean(errors.backupKeepDays)"
            />
            <CFormFeedback invalid>{{ errors.backupKeepDays?.[0] }}</CFormFeedback>
            <CFormText>{{ t('本機與每個遠端目的地都套用同一套規則。') }}</CFormText>
          </CCol>

          <CCol :md="4">
            <CFormLabel for="backup-directory">{{ t('本機存放目錄') }}</CFormLabel>
            <CFormInput
              id="backup-directory"
              v-model="form.backupDirectory"
              class="font-monospace"
              :placeholder="backupDefaultDirectory"
              :invalid="Boolean(errors.backupDirectory)"
            />
            <CFormFeedback invalid>{{ errors.backupDirectory?.[0] }}</CFormFeedback>
          </CCol>

          <CCol :xs="12">
            <CFormLabel for="backup-passphrase">{{ t('加密通關密語') }}</CFormLabel>
            <CFormInput
              id="backup-passphrase"
              v-model="form.backupPassphrase"
              type="password"
              minlength="16"
              maxlength="1024"
              autocomplete="new-password"
              class="font-monospace"
              :invalid="Boolean(errors.backupPassphrase)"
            />
            <CFormFeedback invalid>{{ errors.backupPassphrase?.[0] }}</CFormFeedback>
            <CFormText>
              {{ t('所有備份都必須設定至少 16 個字元的通關密語，並使用分塊 AES-256-GCM 認證加密。') }}
              {{ t('舊版 CBC 備份仍可離線解密，但 API 只接受具認證保護的新格式。') }}
            </CFormText>
            <CAlert v-if="hasRemoteTarget && !form.backupPassphrase" color="warning" class="mt-2 mb-0 py-2 small">
              {{ t('已設定遠端目的地，但還沒有通關密語。') }}
            </CAlert>
          </CCol>

          <CCol :xs="12">
            <CFormLabel for="backup-paths">{{ t('額外要備份的路徑') }}</CFormLabel>
            <CFormTextarea id="backup-paths" v-model="form.backupPaths" class="font-monospace" rows="3" />
            <CFormText>{{ t('一行一個絕對路徑；目錄會整個打包，符號連結會跳過。') }}</CFormText>
          </CCol>
        </CRow>

        <hr class="my-4" />

        <div class="d-flex align-items-center gap-2 mb-3">
          <div class="fw-semibold me-auto">{{ t('PostgreSQL') }}</div>
          <CFormSwitch id="backup-pg" v-model="form.backupPostgresEnabled" :label="t('一併備份')" />
        </div>

        <CRow v-if="form.backupPostgresEnabled" class="g-3">
          <CCol :md="4">
            <CFormLabel for="pg-host">{{ t('主機') }}</CFormLabel>
            <CFormInput id="pg-host" v-model="form.backupPostgresHost" class="font-monospace" />
          </CCol>
          <CCol :md="2">
            <CFormLabel for="pg-port">{{ t('連接埠') }}</CFormLabel>
            <CFormInput id="pg-port" v-model.number="form.backupPostgresPort" type="number" />
          </CCol>
          <CCol :md="3">
            <CFormLabel for="pg-user">{{ t('帳號') }}</CFormLabel>
            <CFormInput id="pg-user" v-model="form.backupPostgresUser" class="font-monospace" />
          </CCol>
          <CCol :md="3">
            <CFormLabel for="pg-password">{{ t('密碼') }}</CFormLabel>
            <CFormInput
              id="pg-password"
              v-model="form.backupPostgresPassword"
              type="password"
              autocomplete="new-password"
              class="font-monospace"
            />
          </CCol>
          <CCol :xs="12">
            <CFormLabel for="pg-databases">{{ t('資料庫') }}</CFormLabel>
            <CFormTextarea id="pg-databases" v-model="form.backupPostgresDatabases" class="font-monospace" rows="2" />
            <CFormText>{{ t('一行一個；用 pg_dump 的 custom 格式，還原用 pg_restore。') }}</CFormText>
          </CCol>
          <CCol :xs="12">
            <CFormLabel for="pg-dump-path">{{ t('pg_dump 位置') }}</CFormLabel>
            <CFormInput
              id="pg-dump-path"
              v-model="form.backupPgDumpPath"
              class="font-monospace"
              placeholder="pg_dump"
            />
          </CCol>
        </CRow>

        <hr class="my-4" />

        <div class="d-flex align-items-center gap-2 mb-3">
          <div class="me-auto">
            <div class="fw-semibold">{{ t('備份目的地') }}</div>
            <div class="small text-body-secondary">{{ t('每一種儲存需要的欄位都不一樣，選好類型會顯示對應的設定。') }}</div>
          </div>
          <CButton color="primary" variant="outline" size="sm" @click="newTarget">
            <CIcon icon="cil-plus" class="me-1" />{{ t('新增目的地') }}
          </CButton>
        </div>

        <div class="table-responsive">
          <CTable align="middle" bordered class="mb-0">
            <CTableHead class="text-nowrap">
              <CTableRow>
                <CTableHeaderCell class="bg-body-tertiary">{{ t('名稱') }}</CTableHeaderCell>
                <CTableHeaderCell class="bg-body-tertiary">{{ t('類型') }}</CTableHeaderCell>
                <CTableHeaderCell class="bg-body-tertiary">{{ t('位置') }}</CTableHeaderCell>
                <CTableHeaderCell class="bg-body-tertiary">{{ t('狀態') }}</CTableHeaderCell>
                <CTableHeaderCell class="bg-body-tertiary text-end">{{ t('操作') }}</CTableHeaderCell>
              </CTableRow>
            </CTableHead>
            <CTableBody>
              <CTableRow v-for="target in backupTargetPage.paged" :key="target.id">
                <CTableDataCell class="fw-semibold">{{ target.name }}</CTableDataCell>
                <CTableDataCell>{{ t(providerLabels[target.provider]) }}</CTableDataCell>
                <CTableDataCell class="font-monospace small text-break">
                  {{ target.provider === 's3' ? `${target.bucket ?? ''}/${target.path}` : target.path }}
                </CTableDataCell>
                <CTableDataCell>
                  <CBadge :color="target.isDisabled ? 'secondary' : 'success'">
                    {{ target.isDisabled ? t('停用') : t('啟用') }}
                  </CBadge>
                </CTableDataCell>
                <CTableDataCell class="text-end text-nowrap">
                  <CButton color="secondary" variant="ghost" size="sm" @click="editTarget(target)">
                    {{ t('編輯') }}
                  </CButton>
                  <CButton color="danger" variant="ghost" size="sm" @click="removeTarget(target)">
                    {{ t('刪除') }}
                  </CButton>
                </CTableDataCell>
              </CTableRow>
              <CTableRow v-if="form.backupTargets.length === 0">
                <CTableDataCell colspan="5" class="text-center text-body-secondary py-4">
                  {{ t('還沒有目的地；只設定本機存放目錄的話，備份只會留在這台機器上。') }}
                </CTableDataCell>
              </CTableRow>
            </CTableBody>
          </CTable>
        </div>

        <TablePagination
          plain
          :page="backupTargetPage.page"
          :page-size="backupTargetPage.pageSize"
          :total="backupTargetPage.total"
          :pages="backupTargetPage.pages"
          @update:page="backupTargetPage.setPage"
          @update:page-size="backupTargetPage.setPageSize"
        />

        <hr class="my-4" />

        <div class="d-flex flex-wrap align-items-center gap-2 mb-3">
          <div class="me-auto">
            <div class="fw-semibold">{{ t('備份紀錄') }}</div>
            <div class="small text-body-secondary">{{ t('依「紀錄保留天數」自動清除。') }}</div>
          </div>
          <CButton color="secondary" variant="outline" size="sm" @click="loadBackup">
            <CIcon icon="cil-reload" class="me-1" />{{ t('重新整理') }}
          </CButton>
          <CButton color="primary" size="sm" :disabled="backupRunning" @click="runBackup">
            <CSpinner v-if="backupRunning" size="sm" class="me-2" />{{ t('立即備份') }}
          </CButton>
        </div>

        <div class="table-responsive">
          <CTable align="middle" bordered class="mb-0">
            <CTableHead class="text-nowrap">
              <CTableRow>
                <CTableHeaderCell class="bg-body-tertiary">{{ t('時間') }}</CTableHeaderCell>
                <CTableHeaderCell class="bg-body-tertiary">{{ t('狀態') }}</CTableHeaderCell>
                <CTableHeaderCell class="bg-body-tertiary">{{ t('觸發') }}</CTableHeaderCell>
                <CTableHeaderCell class="bg-body-tertiary">{{ t('大小') }}</CTableHeaderCell>
                <CTableHeaderCell class="bg-body-tertiary">{{ t('耗時') }}</CTableHeaderCell>
                <CTableHeaderCell class="bg-body-tertiary text-end">{{ t('輸出') }}</CTableHeaderCell>
              </CTableRow>
            </CTableHead>
            <CTableBody>
              <template v-for="run in backupRuns" :key="run.id">
                <CTableRow>
                  <CTableDataCell class="text-nowrap text-body-secondary">{{ dateTime(run.startedAt) }}</CTableDataCell>
                  <CTableDataCell>
                    <CBadge
                      :color="run.status === 'Succeeded' ? 'success' : run.status === 'Failed' ? 'danger' : 'info'"
                    >
                      {{ t(run.status === 'Succeeded' ? '成功' : run.status === 'Failed' ? '失敗' : '執行中') }}
                    </CBadge>
                  </CTableDataCell>
                  <CTableDataCell>{{ t(run.triggeredBy === 'schedule' ? '排程' : '手動') }}</CTableDataCell>
                  <CTableDataCell>{{ run.sizeBytes ? formatSize(run.sizeBytes) : '—' }}</CTableDataCell>
                  <CTableDataCell>
                    {{ run.durationSeconds ? `${run.durationSeconds.toFixed(1)} s` : '—' }}
                  </CTableDataCell>
                  <CTableDataCell class="text-end">
                    <CButton
                      color="secondary"
                      variant="ghost"
                      size="sm"
                      @click="expandedRun = expandedRun === run.id ? null : run.id"
                    >
                      {{ expandedRun === run.id ? t('收合') : t('展開') }}
                    </CButton>
                  </CTableDataCell>
                </CTableRow>
                <CTableRow v-if="expandedRun === run.id">
                  <CTableDataCell colspan="6" class="bg-body-tertiary">
                    <pre class="mb-0 small text-break" style="white-space: pre-wrap">{{ run.log }}</pre>
                  </CTableDataCell>
                </CTableRow>
              </template>
              <CTableRow v-if="backupRuns.length === 0">
                <CTableDataCell colspan="6" class="text-center text-body-secondary py-4">
                  {{ t('尚未執行過備份。') }}
                </CTableDataCell>
              </CTableRow>
            </CTableBody>
          </CTable>
        </div>

        <div class="d-flex align-items-center gap-2 mt-4 mb-3">
          <div class="me-auto">
            <div class="fw-semibold">{{ t('從備份還原') }}</div>
            <div class="small text-body-secondary">
              {{ t('備份裡的資料與資料庫種類無關，因此也可以還原到另一種資料庫。') }}
            </div>
          </div>
          <CButton color="secondary" variant="outline" size="sm" @click="loadBackupFiles">
            <CIcon icon="cil-reload" class="me-1" />{{ t('重新整理') }}
          </CButton>
        </div>

        <CAlert color="warning" class="d-flex align-items-start gap-2">
          <CIcon icon="cil-warning" class="flex-shrink-0 mt-1" />
          <div>{{ t('還原會清空目前所有資料，再寫回備份裡的內容，無法復原。') }}</div>
        </CAlert>

        <CRow class="g-3">
          <CCol :md="6">
            <CFormLabel for="restore-file">{{ t('備份檔') }}</CFormLabel>
            <CFormSelect id="restore-file" v-model="restoreForm.fileName" :disabled="backupFiles.length === 0">
              <option v-for="file in backupFiles" :key="file.name" :value="file.name">
                {{ file.name }}（{{ formatSize(file.sizeBytes) }}）
              </option>
            </CFormSelect>
            <CFormText v-if="backupFiles.length === 0">
              {{ t('{directory} 裡沒有備份檔。', { directory: backupDirectory }) }}
            </CFormText>
            <CFormText v-else>{{ backupDirectory }}</CFormText>
          </CCol>

          <CCol :md="6">
            <CFormLabel for="restore-passphrase">{{ t('通關密語') }}</CFormLabel>
            <CFormInput
              id="restore-passphrase"
              v-model="restoreForm.passphrase"
              type="password"
              minlength="16"
              maxlength="1024"
              autocomplete="off"
              :placeholder="t('留空則使用設定裡的通關密語')"
            />
          </CCol>

          <CCol :xs="12">
            <CFormSwitch
              id="restore-artifacts"
              v-model="restoreForm.includeArtifacts"
              :label="t('一併還原產出檔案')"
            />
            <CFormText>{{ t('還原會暫停新建置；若仍有建置在執行，必須等全部結束後再試。') }}</CFormText>
          </CCol>

          <CCol :xs="12">
            <CButton
              color="danger"
              :disabled="restoring || !restoreForm.fileName"
              @click="restoreConfirm = true"
            >
              <CSpinner v-if="restoring" size="sm" class="me-2" />{{ t('還原') }}
            </CButton>
          </CCol>
        </CRow>

        <CModal :visible="restoreConfirm" @close="restoreConfirm = false">
          <CModalHeader>
            <CModalTitle>{{ t('確認還原') }}</CModalTitle>
          </CModalHeader>
          <CModalBody>
            {{ t('確定要用 {file} 覆蓋目前所有資料嗎？這個動作無法復原。', { file: restoreForm.fileName }) }}
          </CModalBody>
          <CModalFooter>
            <CButton color="secondary" variant="outline" @click="restoreConfirm = false">
              {{ t('取消') }}
            </CButton>
            <CButton color="danger" @click="restore">{{ t('還原') }}</CButton>
          </CModalFooter>
        </CModal>
      </template>

      <template v-else-if="activeTab === 'security'">
        <div class="d-flex flex-wrap align-items-center justify-content-between gap-2 p-3 mb-3 border rounded bg-body-tertiary">
          <div>
            <div class="fw-semibold">{{ t('安全中心') }}</div>
            <div class="small text-body-secondary">
              {{ t('可前往安全中心一覽全站安全態勢健檢、高權限任務／外掛／金鑰盤點，以及解除登入鎖定。') }}
            </div>
          </div>
          <RouterLink class="btn btn-primary btn-sm" to="/security">
            {{ t('前往安全中心') }}
          </RouterLink>
        </div>
        <CRow class="g-3">
          <CCol :xs="12">
            <CFormSwitch
              id="require-signin"
              v-model="form.requireSignIn"
              :disabled="auth.users.length === 0"
              :label="t('啟用管理端登入')"
            />
            <CFormText v-if="auth.users.length === 0">{{ t('啟用登入前，請先建立一個管理帳號。') }}</CFormText>
            <CFormSwitch
              id="require-otp"
              v-model="form.requireOtp"
              class="mt-2"
              :disabled="!form.requireSignIn"
              :label="t('要求所有帳號啟用一次性密碼')"
            />
            <CFormText>{{ t('開啟後，尚未設定的帳號登入後仍可使用，但無法自行停用一次性密碼。') }}</CFormText>
            <CFormSwitch id="audit-log" v-model="form.auditLog" class="mt-2" :label="t('記錄操作稽核紀錄')" />
            <CFormText>{{ t('關閉或開啟稽核本身一定會留下一筆紀錄，並發出安全告警。') }}</CFormText>
            <CFormLabel for="audit-retention" class="mt-3">{{ t('稽核保留天數') }}</CFormLabel>
            <CFormInput
              id="audit-retention"
              v-model.number="form.auditRetentionDays"
              type="number"
              min="30"
              max="3650"
              :invalid="Boolean(errors.auditRetentionDays)"
            />
            <CFormText>{{ t('至少 30 天，與建置紀錄的保留天數分開計算。') }}</CFormText>
            <div class="d-flex align-items-center gap-2 mt-2">
              <CFormSwitch id="mask-secrets" v-model="form.maskSecrets" :label="t('在建置輸出中遮蔽機密值')" />
              <CBadge v-if="!form.maskSecrets" color="warning" text-color="dark">{{ t('機密遮蔽已關閉') }}</CBadge>
            </div>
            <CFormText>{{ t('關閉後，建置輸出會原樣顯示機密變數的內容。') }}</CFormText>
            <CFormSwitch
              id="allow-operator-ai"
              v-model="form.allowOperatorAi"
              class="mt-2"
              :label="t('允許操作者使用 AI 功能')"
            />
            <CFormText>{{ t('關閉後，只有管理員能使用 AI 審閱腳本與分析建置；這些功能會把腳本與建置輸出送到 AI 供應商。') }}</CFormText>
          </CCol>

          <CCol :md="6">
            <CFormLabel for="cors">{{ t('允許的 CORS 來源') }}</CFormLabel>
            <CFormTextarea id="cors" v-model="form.corsOrigins" class="font-monospace" rows="3" />
            <CFormText>{{ t('一行一個來源，儲存後立即生效。') }}</CFormText>
          </CCol>

          <CCol :md="6">
            <CFormLabel for="mcp-allowed-addresses">{{ t('MCP 允許的來源位址') }}</CFormLabel>
            <CFormTextarea
              id="mcp-allowed-addresses"
              v-model="form.mcpAllowedAddresses"
              class="font-monospace"
              rows="3"
              placeholder="127.0.0.1/32&#10;::1/128&#10;203.0.113.0/24"
            />
            <CFormText>{{ t('每行一個 IP 或 CIDR；空白＝不限制。設定後只有列出的位址能連 /mcp，本機也要列出 127.0.0.1/32 與 ::1/128 才連得到。') }}</CFormText>
          </CCol>

          <CCol :md="6">
            <CFormLabel for="webhook-secret">{{ t('Webhook 密鑰') }}</CFormLabel>
            <CFormInput
              id="webhook-secret"
              v-model="form.webhookSecret"
              class="font-monospace"
              type="password"
              autocomplete="new-password"
            />
            <CFormText>{{ t('設定後，GitHub 需帶 X-Hub-Signature-256，其他來源可用 X-Walis-Token。') }}</CFormText>

            <CFormLabel for="webhook-url" class="mt-3">{{ t('推送 webhook 網址') }}</CFormLabel>
            <CInputGroup>
              <CFormInput id="webhook-url" class="font-monospace" :model-value="webhookUrl" readonly />
              <CButton color="secondary" variant="outline" @click="copyWebhookUrl">{{ t('複製') }}</CButton>
            </CInputGroup>
            <CFormText>{{ t('把這個網址填到 Git 服務的 webhook 設定，推送時會觸發對應分支的任務。') }}</CFormText>
          </CCol>

          <CCol :md="6">
            <CFormLabel for="notification-signing-secret">{{ t('通知簽章密鑰') }}</CFormLabel>
            <CFormInput
              id="notification-signing-secret"
              v-model="form.notificationSigningSecret"
              class="font-monospace"
              type="password"
              autocomplete="new-password"
            />
            <CFormText>
              {{ t('設定後（32 到 1,024 個字元），webhook 通知會帶 Walis-Signature: t=<unix 秒>,v1=<hex> 與 Walis-Event-Id 標頭；v1 是以此密鑰對「t.原始內容」計算的 HMAC-SHA256。留空則不簽章。') }}
            </CFormText>
          </CCol>
        </CRow>

        <template v-if="secretsStatus">
          <hr class="my-4" />

          <div class="d-flex flex-wrap align-items-start gap-2">
            <div class="me-auto">
              <div class="fw-semibold">{{ t('機密加密') }}</div>
              <div class="small text-body-secondary">
                {{ t('資料庫裡的 AI 金鑰、機密變數、設定密碼與 TOTP 秘密都以此金鑰加密；備份不含金鑰，還原到其他機器前要先帶過去。') }}
              </div>
              <div class="small mt-1">
                {{ t('金鑰來源：{source}', { source: secretsStatus.keySource }) }}
                <code v-if="secretsStatus.keyPath" class="ms-1">{{ secretsStatus.keyPath }}</code>
              </div>
              <div v-if="secretsStatus.count === 0" class="small text-success mt-1">
                {{ t('所有已加密的機密都能用目前的金鑰解密。') }}
              </div>
              <div v-else class="small text-danger mt-1">
                {{ t('有 {count} 筆機密無法用目前的金鑰解密：', { count: secretsStatus.count }) }}
                <code v-for="item in secretsStatus.undecryptable" :key="`${item.table}:${item.item}`" class="ms-1">
                  {{ item.table }}:{{ item.item }}
                </code>
                <div class="text-body-secondary mt-1">
                  {{ t('清除後這些值會變成未設定，TOTP 會連同備援碼一併移除；若原本的金鑰還在，把它放回資料目錄比較好。') }}
                </div>
              </div>
            </div>
            <CButton
              v-if="secretsStatus.count > 0"
              color="danger"
              variant="outline"
              size="sm"
              :disabled="resettingSecrets"
              @click="resetUndecryptableSecrets"
            >
              {{ t('清除無法解密的機密') }}
            </CButton>
          </div>
        </template>

        <hr class="my-4" />

        <div class="d-flex flex-wrap align-items-center gap-2">
          <div class="me-auto">
            <div class="fw-semibold">{{ t('帳號管理') }}</div>
            <div class="small text-body-secondary">
              {{ t('目前有 {count} 個帳號；新增、角色與一次性密碼都在帳號頁面設定。', { count: auth.users.length }) }}
            </div>
          </div>
          <RouterLink class="btn btn-outline-secondary btn-sm" to="/accounts">
            {{ t('前往帳號管理') }}
          </RouterLink>
        </div>
      </template>

      <template v-else>
        <div class="d-flex flex-wrap align-items-center gap-2 mb-3">
          <span class="text-body-secondary small me-auto">
            {{ t('稽核紀錄會依「稽核保留天數」自動清除。') }}
          </span>
          <a class="btn btn-outline-secondary btn-sm" :href="auditExportUrl" download>
            <CIcon icon="cil-cloud-download" class="me-1" />{{ t('匯出 CSV') }}
          </a>
          <CButton color="secondary" variant="outline" size="sm" :disabled="auditLoading" @click="loadAudit">
            <CIcon icon="cil-reload" class="me-1" />{{ t('重新整理') }}
          </CButton>
        </div>

        <CRow class="g-2 mb-3 align-items-end">
          <CCol :md="2">
            <CFormLabel for="audit-actor">{{ t('操作者') }}</CFormLabel>
            <CFormInput id="audit-actor" v-model="auditFilter.actor" @keyup.enter="applyAuditFilter" />
          </CCol>
          <CCol :md="2">
            <CFormLabel for="audit-entity-type">{{ t('對象') }}</CFormLabel>
            <CFormSelect id="audit-entity-type" v-model="auditFilter.entityType">
              <option value="">{{ t('全部') }}</option>
              <option v-for="(label, key) in entityLabels" :key="key" :value="key">{{ t(label) }}</option>
            </CFormSelect>
          </CCol>
          <CCol :md="3">
            <CFormLabel for="audit-entity-id">{{ t('對象識別碼') }}</CFormLabel>
            <CFormInput id="audit-entity-id" v-model="auditFilter.entityId" class="font-monospace" @keyup.enter="applyAuditFilter" />
          </CCol>
          <CCol :md="2">
            <CFormLabel for="audit-from">{{ t('起') }}</CFormLabel>
            <CFormInput id="audit-from" v-model="auditFilter.from" type="date" />
          </CCol>
          <CCol :md="2">
            <CFormLabel for="audit-to">{{ t('迄') }}</CFormLabel>
            <CFormInput id="audit-to" v-model="auditFilter.to" type="date" />
          </CCol>
          <CCol :md="1" class="d-flex gap-2">
            <CButton color="primary" size="sm" :disabled="auditLoading" @click="applyAuditFilter">{{ t('查詢') }}</CButton>
            <CButton color="secondary" variant="outline" size="sm" :disabled="auditLoading" @click="clearAuditFilter">
              {{ t('清除') }}
            </CButton>
          </CCol>
        </CRow>

        <div class="table-responsive">
          <CTable align="middle" bordered class="mb-0">
            <CTableHead class="text-nowrap">
              <CTableRow>
                <CTableHeaderCell class="bg-body-tertiary">{{ t('時間') }}</CTableHeaderCell>
                <CTableHeaderCell class="bg-body-tertiary">{{ t('操作者') }}</CTableHeaderCell>
                <CTableHeaderCell class="bg-body-tertiary">{{ t('來源位址') }}</CTableHeaderCell>
                <CTableHeaderCell class="bg-body-tertiary">{{ t('動作') }}</CTableHeaderCell>
                <CTableHeaderCell class="bg-body-tertiary">{{ t('結果') }}</CTableHeaderCell>
                <CTableHeaderCell class="bg-body-tertiary">{{ t('對象') }}</CTableHeaderCell>
                <CTableHeaderCell class="bg-body-tertiary">{{ t('說明') }}</CTableHeaderCell>
              </CTableRow>
            </CTableHead>
            <CTableBody>
              <CTableRow v-for="entry in audit" :key="entry.id">
                <CTableDataCell class="text-body-secondary text-nowrap">{{ dateTime(entry.occurredAt) }}</CTableDataCell>
                <CTableDataCell>{{ entry.actor }}</CTableDataCell>
                <CTableDataCell class="font-monospace small">{{ entry.remoteAddress ?? '—' }}</CTableDataCell>
                <CTableDataCell>{{ t(actionLabels[entry.action] ?? entry.action) }}</CTableDataCell>
                <CTableDataCell>
                  <CBadge :color="entry.outcome === 'success' ? 'success' : entry.outcome === 'denied' ? 'warning' : 'danger'">
                    {{ t(outcomeLabels[entry.outcome] ?? entry.outcome) }}
                  </CBadge>
                </CTableDataCell>
                <CTableDataCell>
                  {{ t(entityLabels[entry.entityType] ?? entry.entityType) }}
                  <div v-if="entry.entityId" class="font-monospace small text-body-secondary text-break">{{ entry.entityId }}</div>
                </CTableDataCell>
                <CTableDataCell class="text-break">
                  {{ entry.summary }}
                  <div v-if="entry.requestId" class="small text-body-secondary font-monospace">{{ entry.requestId }}</div>
                </CTableDataCell>
              </CTableRow>
              <CTableRow v-if="audit.length === 0">
                <CTableDataCell colspan="7" class="text-center text-body-secondary py-5">
                  {{ t('尚未有稽核紀錄。') }}
                </CTableDataCell>
              </CTableRow>
            </CTableBody>
          </CTable>
        </div>
      </template>
    </CCardBody>
    <TablePagination
      v-if="activeTab === 'audit'"
      :page="auditPageNumber"
      :page-size="auditPageSize"
      :total="auditTotal"
      :pages="auditPages"
      :page-sizes="PAGE_SIZES"
      :summary="t('顯示第 {from}–{to} 筆，共 {total} 筆稽核紀錄', { from: auditRangeFrom, to: auditRangeTo, total: auditTotal })"
      @update:page="(value: number) => (auditPageNumber = value)"
      @update:page-size="(value: number) => (auditPageSize = value)"
    />

  </CCard>

  <PageActions v-if="activeTab !== 'audit'">
    <span class="text-body-secondary small d-none d-md-inline">{{ t('設定會立即套用到排程、建置與通知。') }}</span>
    <CButton color="secondary" variant="outline" size="sm" :disabled="saving" @click="revert">{{ t('還原') }}</CButton>
    <CButton color="primary" size="sm" :disabled="saving" @click="save">
      <CSpinner v-if="saving" size="sm" class="me-2" />{{ t('儲存設定') }}
    </CButton>
  </PageActions>

  <BackupTargetWizard :visible="wizardVisible" :target="targetEditor" @close="wizardVisible = false" @apply="applyTarget" />

</template>
