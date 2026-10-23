<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useI18n } from 'vue-i18n'
import TablePagination from '../components/TablePagination.vue'
import PageActions from '../components/PageActions.vue'
import { DEFAULT_PAGE_SIZE, PAGE_SIZES, usePagedRows } from '../composables/usePagedRows'
import { useFormat } from '../composables/useFormat'
import { ApiError, api } from '../services/api'
import { useAuthStore } from '../stores/auth'
import { useSettingsStore } from '../stores/settings'
import { useToastStore } from '../stores/toasts'
import type {
  AuditEntry,
  AuditFilter,
  SecretsStatus,
  SecurityCenterPayload,
  SecurityPostureCheck,
  WalisSettings
} from '../types'

const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const settings = useSettingsStore()
const toasts = useToastStore()
const { dateTime } = useFormat()

type SecurityTab = 'posture' | 'inventory' | 'threats' | 'policies'

const loading = ref(false)
const saving = ref(false)
const resettingSecrets = ref(false)
const activeTab = ref<SecurityTab>('posture')
const filterUnpassedOnly = ref(false)
const threatKindFilter = ref<'all' | 'locked-out' | 'sign-in-failed' | 'denied' | 'reveal' | 'policy'>('all')

const data = ref<SecurityCenterPayload | null>(null)
const secretsStatus = ref<SecretsStatus | null>(null)
const form = ref<WalisSettings | null>(null)
const errors = ref<Record<string, string[]>>({})

const audit = ref<AuditEntry[]>([])
const auditLoading = ref(false)
const auditTotal = ref(0)
const auditPageNumber = ref(1)
const auditPageSize = ref(DEFAULT_PAGE_SIZE)
const auditFilter = ref({
  actor: '',
  entityType: '',
  entityId: '',
  action: '',
  outcome: '',
  from: '',
  to: ''
})

const auditPages = computed(() => Math.max(1, Math.ceil(auditTotal.value / auditPageSize.value)))
const auditRangeFrom = computed(() =>
  auditTotal.value === 0 ? 0 : (auditPageNumber.value - 1) * auditPageSize.value + 1
)
const auditRangeTo = computed(() => Math.min(auditTotal.value, auditPageNumber.value * auditPageSize.value))

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
  'ai-provider': 'AI 供應商',
  security: '安全中心'
}

const outcomeLabels: Record<string, string> = {
  success: '成功',
  denied: '拒絕',
  failed: '失敗'
}

const riskFlagLabels: Record<string, string> = {
  'unrestricted-ip': '未限制來源 IP',
  'no-expiry': '無到期日',
  'admin-role': '管理員角色',
  stale: '超過 30 天未使用'
}

function cloneSettings(value: WalisSettings): WalisSettings {
  return {
    ...value,
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

const webhookUrl = computed(() => {
  const base = form.value?.baseUrl?.trim().replace(/\/$/, '') ?? ''
  return `${base}/api/webhooks/push`
})

const visibleChecks = computed(() => {
  const checks = data.value?.posture.checks ?? []
  return filterUnpassedOnly.value ? checks.filter((item) => item.status !== 'pass') : checks
})

const inventoryTotal = computed(() => {
  if (!data.value) return 0
  return (
    data.value.inventory.privilegedJobs.length +
    data.value.inventory.trustedPlugins.length +
    data.value.inventory.riskyApiKeys.length
  )
})

const filteredThreats = computed(() => {
  const events = data.value?.threats.recentEvents ?? []
  const kind = threatKindFilter.value
  if (kind === 'all') return events
  if (kind === 'locked-out') return events.filter((item) => item.action === 'locked-out')
  if (kind === 'sign-in-failed') return events.filter((item) => item.action === 'sign-in-failed')
  if (kind === 'denied') return events.filter((item) => item.outcome === 'denied')
  if (kind === 'reveal') return events.filter((item) => item.action === 'reveal')
  return events.filter((item) =>
    ['settings', 'user', 'api-key', 'plugin', 'security'].includes(item.entityType)
  )
})

const threatPage = usePagedRows(() => filteredThreats.value)

const postureColor = computed(() => {
  const status = data.value?.posture.status
  if (status === 'danger') return 'danger'
  if (status === 'warn') return 'warning'
  return 'success'
})

const postureTitle = computed(() => {
  const status = data.value?.posture.status
  if (status === 'danger') return t('安全態勢：高風險')
  if (status === 'warn') return t('安全態勢：需注意')
  return t('安全態勢：良好')
})

const isolationLabel = computed(() => {
  const mode = data.value?.posture.isolationMode
  if (mode === 'sandboxed') return t('沙箱隔離')
  if (mode === 'strict-external') return t('強制外部隔離')
  if (mode === 'unisolated-acknowledged') return t('未隔離（已簽認）')
  return t('未隔離')
})

const isolationNote = computed(() => {
  const mode = data.value?.posture.isolationMode
  if (mode === 'sandboxed') return t('macOS sandbox-exec 啟用中')
  if (mode === 'strict-external') return t('拒絕無邊界執行器')
  if (mode === 'unisolated-acknowledged') return t('管理員已簽認未隔離建置風險')
  return t('建置直接以服務帳號權限執行')
})

async function loadAll(): Promise<void> {
  loading.value = true
  try {
    const [centerPayload, secretsPayload] = await Promise.all([
      api.securityCenter(),
      api.settingsSecrets().catch(() => null),
      auth.loadUsers().catch(() => undefined)
    ])
    data.value = centerPayload
    secretsStatus.value = secretsPayload
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  } finally {
    loading.value = false
  }
}

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
    action: auditFilter.value.action.trim() || undefined,
    outcome: auditFilter.value.outcome.trim() || undefined,
    ...auditRange()
  }
}

const auditExportUrl = computed(() => api.auditExportUrl(currentAuditFilter()))

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
    if (id !== auditRequestId) return
    audit.value = response.items
    auditTotal.value = response.total
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
  if (auditPageNumber.value !== 1) {
    auditPageNumber.value = 1
  } else {
    void loadAudit()
  }
}

function clearAuditFilter(): void {
  auditFilter.value = { actor: '', entityType: '', entityId: '', action: '', outcome: '', from: '', to: '' }
  applyAuditFilter()
}

watch([auditPageNumber, auditPageSize], () => {
  if (activeTab.value === 'policies') {
    void loadAudit()
  }
})

watch(activeTab, (tab) => {
  if (tab === 'policies') {
    void loadAudit()
  }
})

async function handleCheckAction(check: SecurityPostureCheck): Promise<void> {
  if (check.action === 'fix-mcp' && check.status !== 'pass' && form.value) {
    form.value.mcpAllowedAddresses = '127.0.0.1/32\n::1/128'
    await savePolicies()
    return
  }
  if (check.action === 'policies' || check.action === 'fix-mcp') {
    activeTab.value = 'policies'
    return
  }
  if (check.action === 'inventory') {
    activeTab.value = 'inventory'
    return
  }
  if (check.action === 'notifications') {
    await router.push('/notifications')
    return
  }
  if (check.action === 'settings-build') {
    await router.push({ path: '/settings', query: { tab: 'build' } })
    return
  }
  if (check.action === 'settings-backup') {
    await router.push({ path: '/settings', query: { tab: 'backup' } })
  }
}

function checkActionLabel(check: SecurityPostureCheck): string {
  if (check.action === 'fix-mcp' && check.status !== 'pass') {
    return t('限制為本機 IP')
  }
  if (check.action === 'policies' || check.action === 'fix-mcp') {
    return t('安全政策')
  }
  if (check.action === 'inventory') {
    return t('高權限盤點')
  }
  if (check.action === 'notifications') {
    return t('通知設定')
  }
  if (check.action === 'settings-build') {
    return t('建置設定')
  }
  if (check.action === 'settings-backup') {
    return t('備份設定')
  }
  return t('查看')
}

async function revokePluginTrust(pluginId: string, name: string): Promise<void> {
  try {
    await api.setPluginTrusted(pluginId, false)
    toasts.success(t('已撤銷外掛「{name}」的信任授權', { name }))
    await loadAll()
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  }
}

async function deleteRiskyKey(id: string, name: string): Promise<void> {
  try {
    await api.deleteApiKey(id)
    toasts.success(t('已刪除 API 金鑰 {name}', { name }))
    await loadAll()
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  }
}

async function unlockTarget(bucketKey: string, target: string): Promise<void> {
  try {
    await api.unlockSignInLockout(bucketKey)
    toasts.success(t('已解除 {target} 的登入鎖定', { target }))
    await loadAll()
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  }
}

async function resetUndecryptableSecrets(): Promise<void> {
  resettingSecrets.value = true
  try {
    const cleared = await api.resetUndecryptableSecrets()
    toasts.success(t('已清除 {count} 筆無法解密的機密，請重新填寫。', { count: cleared.count }))
    await Promise.all([loadAll(), settings.load(true)])
    form.value = settings.settings ? cloneSettings(settings.settings) : form.value
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  } finally {
    resettingSecrets.value = false
  }
}

function revertPolicies(): void {
  if (settings.settings) {
    form.value = cloneSettings(settings.settings)
    errors.value = {}
    toasts.info(t('已還原為上次儲存的設定'))
  }
}

async function savePolicies(): Promise<void> {
  if (!form.value) return
  saving.value = true
  errors.value = {}
  try {
    await settings.save(form.value)
    await auth.refresh()
    form.value = settings.settings ? cloneSettings(settings.settings) : form.value
    toasts.success(t('安全政策已儲存'))
    await loadAll()
    if (activeTab.value === 'policies') {
      await loadAudit()
    }
  } catch (error) {
    if (error instanceof ApiError) {
      errors.value = error.errors
    }
    toasts.error(error instanceof Error ? error.message : String(error))
  } finally {
    saving.value = false
  }
}

async function copyWebhookUrl(): Promise<void> {
  try {
    await navigator.clipboard.writeText(webhookUrl.value)
    toasts.success(t('已複製 webhook 網址'))
  } catch {
    toasts.error(t('無法存取剪貼簿'))
  }
}

function exportPostureJson(): void {
  if (!data.value) return
  const blob = new Blob([JSON.stringify(data.value, null, 2)], { type: 'application/json' })
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = `walis-security-posture-${new Date().toISOString().slice(0, 10)}.json`
  link.click()
  URL.revokeObjectURL(url)
}

onMounted(async () => {
  const requestedTab = route.query.tab
  if (
    requestedTab === 'posture' ||
    requestedTab === 'inventory' ||
    requestedTab === 'threats' ||
    requestedTab === 'policies'
  ) {
    activeTab.value = requestedTab
  } else if (requestedTab === 'audit' || requestedTab === 'security') {
    activeTab.value = 'policies'
  }
  await loadAll()
  if (activeTab.value === 'policies') {
    await loadAudit()
  }
})
</script>

<template>
  <div v-if="data">
    <CAlert
      :color="postureColor"
      class="border-start border-4 d-flex flex-wrap align-items-center gap-3 mb-4"
    >
      <div class="d-flex align-items-center gap-3 me-auto">
        <span
          class="d-inline-flex align-items-center justify-content-center rounded flex-shrink-0"
          :class="
            postureColor === 'success'
              ? 'bg-success text-white'
              : postureColor === 'danger'
                ? 'bg-danger text-white'
                : 'bg-warning text-dark'
          "
          style="width: 3rem; height: 3rem"
        >
          <CIcon icon="cil-shield-alt" size="xl" />
        </span>
        <div>
          <div class="d-flex flex-wrap align-items-center gap-2">
            <span class="fw-semibold fs-5">{{ postureTitle }}</span>
            <CBadge :color="postureColor" :text-color="postureColor === 'warning' ? 'dark' : undefined">
              {{
                t('{passed} / {total} 項通過', {
                  passed: data.posture.passedChecks,
                  total: data.posture.totalChecks
                })
              }}
            </CBadge>
          </div>
          <div class="small mt-1">
            <template v-if="data.posture.warnChecks === 0 && data.posture.dangerChecks === 0 && data.threats.activeLockouts.length === 0">
              {{ t('所有安全健檢項目皆已通過，目前無生效中的登入鎖定。') }}
            </template>
            <template v-else>
              {{
                t('尚有 {warn} 項待強化檢查、{danger} 項高風險項目，以及 {lockouts} 筆生效中的登入鎖定。', {
                  warn: data.posture.warnChecks,
                  danger: data.posture.dangerChecks,
                  lockouts: data.threats.activeLockouts.length
                })
              }}
            </template>
          </div>
        </div>
      </div>
      <div class="d-flex flex-wrap gap-2">
        <CButton color="dark" variant="outline" size="sm" :disabled="loading" @click="loadAll">
          <CIcon icon="cil-reload" class="me-1" />{{ t('重新掃描') }}
        </CButton>
        <CButton color="dark" size="sm" @click="exportPostureJson">
          <CIcon icon="cil-cloud-download" class="me-1" />{{ t('匯出盤點報告') }}
        </CButton>
      </div>
    </CAlert>

    <CRow class="g-4 mb-4">
      <CCol :sm="6" :xl="3">
        <CCard class="h-100">
          <CCardBody>
            <div class="d-flex justify-content-between align-items-start">
              <div>
                <div class="text-body-secondary small text-uppercase fw-semibold">
                  {{ t('安全健檢通過率') }}
                </div>
                <div class="fs-4 fw-semibold mt-1">
                  {{ data.posture.passedChecks }} / {{ data.posture.totalChecks }}
                </div>
              </div>
              <span
                class="p-2 rounded"
                :class="
                  data.posture.dangerChecks > 0
                    ? 'bg-danger bg-opacity-10 text-danger'
                    : data.posture.warnChecks > 0
                      ? 'bg-warning bg-opacity-10 text-warning'
                      : 'bg-success bg-opacity-10 text-success'
                "
              >
                <CIcon icon="cil-check-circle" size="xl" />
              </span>
            </div>
            <div class="small text-body-secondary mt-2">
              {{
                t('{warn} 項建議強化 · {danger} 項高風險', {
                  warn: data.posture.warnChecks,
                  danger: data.posture.dangerChecks
                })
              }}
            </div>
          </CCardBody>
        </CCard>
      </CCol>

      <CCol :sm="6" :xl="3">
        <CCard class="h-100">
          <CCardBody>
            <div class="d-flex justify-content-between align-items-start">
              <div>
                <div class="text-body-secondary small text-uppercase fw-semibold">
                  {{ t('建置隔離邊界') }}
                </div>
                <div
                  class="fs-4 fw-semibold mt-1"
                  :class="
                    data.posture.isolationMode === 'sandboxed' || data.posture.isolationMode === 'strict-external'
                      ? 'text-success'
                      : 'text-warning'
                  "
                >
                  {{ isolationLabel }}
                </div>
              </div>
              <span class="p-2 rounded bg-success bg-opacity-10 text-success">
                <CIcon icon="cil-lock-locked" size="xl" />
              </span>
            </div>
            <div class="small text-body-secondary mt-2">{{ isolationNote }}</div>
          </CCardBody>
        </CCard>
      </CCol>

      <CCol :sm="6" :xl="3">
        <CCard class="h-100">
          <CCardBody>
            <div class="d-flex justify-content-between align-items-start">
              <div>
                <div class="text-body-secondary small text-uppercase fw-semibold">
                  {{ t('高權限與例外項目') }}
                </div>
                <div class="fs-4 fw-semibold mt-1">{{ t('{count} 項', { count: inventoryTotal }) }}</div>
              </div>
              <span class="p-2 rounded bg-info bg-opacity-10 text-info">
                <CIcon icon="cil-layers" size="xl" />
              </span>
            </div>
            <div class="small text-body-secondary mt-2">
              {{
                t('{jobs} 特權任務 · {plugins} 信任外掛 · {keys} 風險金鑰', {
                  jobs: data.inventory.privilegedJobs.length,
                  plugins: data.inventory.trustedPlugins.length,
                  keys: data.inventory.riskyApiKeys.length
                })
              }}
            </div>
          </CCardBody>
        </CCard>
      </CCol>

      <CCol :sm="6" :xl="3">
        <CCard class="h-100">
          <CCardBody>
            <div class="d-flex justify-content-between align-items-start">
              <div>
                <div class="text-body-secondary small text-uppercase fw-semibold">
                  {{ t('近 24h 威脅與鎖定') }}
                </div>
                <div
                  class="fs-4 fw-semibold mt-1"
                  :class="data.threats.activeLockouts.length > 0 ? 'text-danger' : 'text-success'"
                >
                  {{ t('{count} 筆鎖定中', { count: data.threats.activeLockouts.length }) }}
                </div>
              </div>
              <span class="p-2 rounded bg-danger bg-opacity-10 text-danger">
                <CIcon icon="cil-warning" size="xl" />
              </span>
            </div>
            <div class="small text-body-secondary mt-2">
              {{
                t('登入失敗 {failures} 次 · 權限拒絕 {denied} 次', {
                  failures: data.threats.metrics24h.signInFailures,
                  denied: data.threats.metrics24h.denied
                })
              }}
            </div>
          </CCardBody>
        </CCard>
      </CCol>
    </CRow>

    <CCard class="mb-4">
      <CCardHeader class="p-0">
        <CNav variant="tabs" class="card-header-tabs mx-0 px-3 pt-3">
          <CNavItem>
            <CNavLink as="button" type="button" :active="activeTab === 'posture'" @click="activeTab = 'posture'">
              {{ t('態勢健檢') }}
              <CBadge
                class="ms-1"
                shape="rounded-pill"
                :color="
                  data.posture.dangerChecks > 0
                    ? 'danger'
                    : data.posture.warnChecks > 0
                      ? 'warning'
                      : 'success'
                "
                :text-color="data.posture.warnChecks > 0 && data.posture.dangerChecks === 0 ? 'dark' : undefined"
              >
                {{ data.posture.warnChecks + data.posture.dangerChecks }}
              </CBadge>
            </CNavLink>
          </CNavItem>
          <CNavItem>
            <CNavLink as="button" type="button" :active="activeTab === 'inventory'" @click="activeTab = 'inventory'">
              {{ t('高權限與例外盤點') }}
              <CBadge class="ms-1" shape="rounded-pill" color="secondary">{{ inventoryTotal }}</CBadge>
            </CNavLink>
          </CNavItem>
          <CNavItem>
            <CNavLink as="button" type="button" :active="activeTab === 'threats'" @click="activeTab = 'threats'">
              {{ t('威脅與鎖定管理') }}
              <CBadge
                class="ms-1"
                shape="rounded-pill"
                :color="data.threats.activeLockouts.length > 0 ? 'danger' : 'secondary'"
              >
                {{ data.threats.activeLockouts.length }}
              </CBadge>
            </CNavLink>
          </CNavItem>
          <CNavItem>
            <CNavLink as="button" type="button" :active="activeTab === 'policies'" @click="activeTab = 'policies'">
              {{ t('安全政策與稽核日誌') }}
            </CNavLink>
          </CNavItem>
        </CNav>
      </CCardHeader>

      <CCardBody>
        <template v-if="activeTab === 'posture'">
          <div class="d-flex flex-wrap align-items-center justify-content-between gap-2 mb-3">
            <div class="text-body-secondary small">
              {{ t('即時檢查認證、建置沙箱、靜態機密加密與網路邊界設定；點擊右側按鈕可前往對應設定或立即修正。') }}
            </div>
            <CFormSwitch
              id="filter-unpassed-checks"
              v-model="filterUnpassedOnly"
              :label="t('只顯示待強化項目')"
            />
          </div>

          <div class="table-responsive">
            <CTable align="middle" bordered class="mb-0">
              <CTableHead class="text-nowrap">
                <CTableRow>
                  <CTableHeaderCell class="bg-body-tertiary" style="width: 7rem">{{ t('狀態') }}</CTableHeaderCell>
                  <CTableHeaderCell class="bg-body-tertiary" style="width: 9rem">{{ t('領域') }}</CTableHeaderCell>
                  <CTableHeaderCell class="bg-body-tertiary">{{ t('檢查項目與目前狀態') }}</CTableHeaderCell>
                  <CTableHeaderCell class="bg-body-tertiary text-end" style="width: 11rem">
                    {{ t('處置動作') }}
                  </CTableHeaderCell>
                </CTableRow>
              </CTableHead>
              <CTableBody>
                <CTableRow v-for="check in visibleChecks" :key="check.id">
                  <CTableDataCell>
                    <CBadge
                      :color="
                        check.status === 'pass' ? 'success' : check.status === 'danger' ? 'danger' : 'warning'
                      "
                      :text-color="check.status === 'warn' ? 'dark' : undefined"
                    >
                      {{
                        t(check.status === 'pass' ? '通過' : check.status === 'danger' ? '高風險' : '建議強化')
                      }}
                    </CBadge>
                  </CTableDataCell>
                  <CTableDataCell>
                    <CBadge color="light" text-color="dark" class="border">{{ t(check.domain) }}</CBadge>
                  </CTableDataCell>
                  <CTableDataCell>
                    <div class="fw-semibold">{{ t(check.title) }}</div>
                    <div class="small text-body-secondary">{{ check.detail }}</div>
                  </CTableDataCell>
                  <CTableDataCell class="text-end text-nowrap">
                    <CButton
                      :color="check.status === 'pass' ? 'secondary' : 'primary'"
                      :variant="check.status === 'pass' ? 'outline' : undefined"
                      size="sm"
                      :disabled="saving"
                      @click="handleCheckAction(check)"
                    >
                      {{ checkActionLabel(check) }}
                    </CButton>
                  </CTableDataCell>
                </CTableRow>
                <CTableRow v-if="visibleChecks.length === 0">
                  <CTableDataCell colspan="4" class="text-center text-body-secondary py-4">
                    {{ t('所有安全檢查項目皆已通過！') }}
                  </CTableDataCell>
                </CTableRow>
              </CTableBody>
            </CTable>
          </div>
        </template>

        <template v-else-if="activeTab === 'inventory'">
          <CRow class="g-4">
            <CCol :xs="12">
              <div class="mb-2">
                <div class="fw-semibold">{{ t('免沙箱與可連外任務（Privileged Jobs）') }}</div>
                <div class="small text-body-secondary">
                  {{ t('標記 trustedUnsandboxed（不套用沙箱）或未開啟 denyNetwork（允許對外連線）的建置任務。') }}
                </div>
              </div>
              <div class="table-responsive">
                <CTable align="middle" bordered small class="mb-0">
                  <CTableHead class="text-nowrap">
                    <CTableRow>
                      <CTableHeaderCell class="bg-body-tertiary">{{ t('任務名稱') }}</CTableHeaderCell>
                      <CTableHeaderCell class="bg-body-tertiary">{{ t('執行器') }}</CTableHeaderCell>
                      <CTableHeaderCell class="bg-body-tertiary">{{ t('沙箱與網路邊界') }}</CTableHeaderCell>
                      <CTableHeaderCell class="bg-body-tertiary">{{ t('最低執行角色') }}</CTableHeaderCell>
                      <CTableHeaderCell class="bg-body-tertiary text-end">{{ t('操作') }}</CTableHeaderCell>
                    </CTableRow>
                  </CTableHead>
                  <CTableBody>
                    <CTableRow v-for="job in data.inventory.privilegedJobs" :key="job.id">
                      <CTableDataCell class="fw-semibold">
                        {{ job.name }}
                        <span v-if="job.isDisabled" class="badge text-bg-secondary ms-1">{{ t('已停用') }}</span>
                      </CTableDataCell>
                      <CTableDataCell class="text-body-secondary">{{ job.runner ?? t('本機執行器') }}</CTableDataCell>
                      <CTableDataCell>
                        <CBadge v-if="job.trustedUnsandboxed" color="danger" class="me-1">
                          {{ t('不套用沙箱（trustedUnsandboxed）') }}
                        </CBadge>
                        <CBadge v-if="!job.denyNetwork" color="warning" text-color="dark">
                          {{ t('允許對外網路') }}
                        </CBadge>
                      </CTableDataCell>
                      <CTableDataCell class="font-monospace small">{{ job.runRole }}</CTableDataCell>
                      <CTableDataCell class="text-end">
                        <RouterLink class="btn btn-outline-secondary btn-sm" :to="`/jobs/${job.id}/edit`">
                          {{ t('編輯任務') }}
                        </RouterLink>
                      </CTableDataCell>
                    </CTableRow>
                    <CTableRow v-if="data.inventory.privilegedJobs.length === 0">
                      <CTableDataCell colspan="5" class="text-center text-body-secondary py-3">
                        {{ t('所有任務皆在沙箱內執行且已封鎖對外網路。') }}
                      </CTableDataCell>
                    </CTableRow>
                  </CTableBody>
                </CTable>
              </div>
            </CCol>

            <CCol :md="6">
              <div class="mb-2">
                <div class="fw-semibold">{{ t('受信任外掛（Trusted Plugins）') }}</div>
                <div class="small text-body-secondary">
                  {{ t('已核准 isTrusted，允許在編譯建置步驟時呼叫 http.fetch 與注入機密標頭。') }}
                </div>
              </div>
              <div class="table-responsive">
                <CTable align="middle" bordered small class="mb-0">
                  <CTableHead class="text-nowrap">
                    <CTableRow>
                      <CTableHeaderCell class="bg-body-tertiary">{{ t('外掛') }}</CTableHeaderCell>
                      <CTableHeaderCell class="bg-body-tertiary">{{ t('允許連線與機密') }}</CTableHeaderCell>
                      <CTableHeaderCell class="bg-body-tertiary text-end">{{ t('操作') }}</CTableHeaderCell>
                    </CTableRow>
                  </CTableHead>
                  <CTableBody>
                    <CTableRow v-for="plugin in data.inventory.trustedPlugins" :key="plugin.pluginId">
                      <CTableDataCell>
                        <div class="fw-semibold">
                          {{ plugin.name }}
                          <span class="text-body-secondary small">v{{ plugin.version }}</span>
                        </div>
                        <div class="font-monospace small text-body-secondary">{{ plugin.pluginId }}</div>
                      </CTableDataCell>
                      <CTableDataCell class="small">
                        <div v-if="plugin.httpPermissions.length > 0">
                          HTTP: <code>{{ plugin.httpPermissions.join(', ') }}</code>
                        </div>
                        <div v-if="plugin.secretPermissions.length > 0">
                          Secrets: <code>{{ plugin.secretPermissions.join(', ') }}</code>
                        </div>
                      </CTableDataCell>
                      <CTableDataCell class="text-end text-nowrap">
                        <CButton
                          color="danger"
                          variant="outline"
                          size="sm"
                          @click="revokePluginTrust(plugin.pluginId, plugin.name)"
                        >
                          {{ t('撤銷信任') }}
                        </CButton>
                      </CTableDataCell>
                    </CTableRow>
                    <CTableRow v-if="data.inventory.trustedPlugins.length === 0">
                      <CTableDataCell colspan="3" class="text-center text-body-secondary py-3">
                        {{ t('目前無受信任外掛。') }}
                      </CTableDataCell>
                    </CTableRow>
                  </CTableBody>
                </CTable>
              </div>
            </CCol>

            <CCol :md="6">
              <div class="mb-2">
                <div class="fw-semibold">{{ t('高風險 API 金鑰盤點') }}</div>
                <div class="small text-body-secondary">
                  {{ t('標示未限制來源 IP（allowedAddresses 空白）、無到期日或擁有管理員角色的啟用金鑰。') }}
                </div>
              </div>
              <div class="table-responsive">
                <CTable align="middle" bordered small class="mb-0">
                  <CTableHead class="text-nowrap">
                    <CTableRow>
                      <CTableHeaderCell class="bg-body-tertiary">{{ t('金鑰名稱') }}</CTableHeaderCell>
                      <CTableHeaderCell class="bg-body-tertiary">{{ t('風險標記') }}</CTableHeaderCell>
                      <CTableHeaderCell class="bg-body-tertiary text-end">{{ t('操作') }}</CTableHeaderCell>
                    </CTableRow>
                  </CTableHead>
                  <CTableBody>
                    <CTableRow v-for="key in data.inventory.riskyApiKeys" :key="key.id">
                      <CTableDataCell>
                        <div class="fw-semibold">{{ key.name }}</div>
                        <div class="small font-monospace text-body-secondary">
                          {{ key.prefix }}… · {{ key.scope }} ({{ key.role }})
                        </div>
                      </CTableDataCell>
                      <CTableDataCell>
                        <CBadge
                          v-for="flag in key.riskFlags"
                          :key="flag"
                          color="warning"
                          text-color="dark"
                          class="me-1 mb-1"
                        >
                          {{ t(riskFlagLabels[flag] ?? flag) }}
                        </CBadge>
                      </CTableDataCell>
                      <CTableDataCell class="text-end text-nowrap">
                        <CButton color="danger" variant="outline" size="sm" @click="deleteRiskyKey(key.id, key.name)">
                          {{ t('撤銷金鑰') }}
                        </CButton>
                      </CTableDataCell>
                    </CTableRow>
                    <CTableRow v-if="data.inventory.riskyApiKeys.length === 0">
                      <CTableDataCell colspan="3" class="text-center text-body-secondary py-3">
                        {{ t('所有啟用中的 API 金鑰皆已設定來源 IP 限制與效期。') }}
                      </CTableDataCell>
                    </CTableRow>
                  </CTableBody>
                </CTable>
              </div>
            </CCol>

            <CCol :xs="12">
              <div class="d-flex flex-wrap align-items-center justify-content-between gap-2 mb-2">
                <div>
                  <div class="fw-semibold">{{ t('帳號權限與雙因素驗證（OTP）覆蓋狀態') }}</div>
                  <div class="small text-body-secondary">
                    {{ t('檢視所有管理員、操作者與檢視者帳號是否已完成 TOTP 綁定。') }}
                  </div>
                </div>
                <RouterLink class="btn btn-outline-secondary btn-sm" to="/accounts">
                  {{ t('前往帳號管理') }}
                </RouterLink>
              </div>
              <div class="table-responsive">
                <CTable align="middle" bordered small class="mb-0">
                  <CTableHead class="text-nowrap">
                    <CTableRow>
                      <CTableHeaderCell class="bg-body-tertiary">{{ t('帳號') }}</CTableHeaderCell>
                      <CTableHeaderCell class="bg-body-tertiary">{{ t('角色') }}</CTableHeaderCell>
                      <CTableHeaderCell class="bg-body-tertiary">{{ t('OTP 狀態') }}</CTableHeaderCell>
                      <CTableHeaderCell class="bg-body-tertiary">{{ t('最後登入') }}</CTableHeaderCell>
                    </CTableRow>
                  </CTableHead>
                  <CTableBody>
                    <CTableRow v-for="acc in data.inventory.accounts" :key="acc.id">
                      <CTableDataCell>
                        <span class="fw-semibold">{{ acc.displayName }}</span>
                        <span class="text-body-secondary font-monospace small ms-1">(@{{ acc.username }})</span>
                        <CBadge v-if="acc.isDisabled" color="secondary" class="ms-1">{{ t('已停用') }}</CBadge>
                      </CTableDataCell>
                      <CTableDataCell>
                        <CBadge :color="acc.role === 'admin' ? 'primary' : 'secondary'">{{ acc.role }}</CBadge>
                      </CTableDataCell>
                      <CTableDataCell>
                        <CBadge :color="acc.hasOtp ? 'success' : 'warning'" :text-color="acc.hasOtp ? undefined : 'dark'">
                          {{ acc.hasOtp ? t('已綁定 TOTP') : t('尚未綁定') }}
                        </CBadge>
                      </CTableDataCell>
                      <CTableDataCell class="small text-body-secondary">
                        {{ acc.lastLoginAt ? dateTime(acc.lastLoginAt) : '—' }}
                      </CTableDataCell>
                    </CTableRow>
                  </CTableBody>
                </CTable>
              </div>
            </CCol>
          </CRow>
        </template>

        <template v-else-if="activeTab === 'threats'">
          <div class="mb-4">
            <div class="mb-2">
              <div class="fw-semibold">{{ t('目前生效中的登入鎖定（SignInRateLimits）') }}</div>
              <div class="small text-body-secondary">
                {{ t('15 分鐘內密碼或 OTP 連續失敗達上限（帳號 5 次／來源 IP 100 次）時自動鎖定 15 分鐘；管理員可手動解除。') }}
              </div>
            </div>
            <div class="table-responsive">
              <CTable align="middle" bordered small class="mb-0">
                <CTableHead class="text-nowrap">
                  <CTableRow>
                    <CTableHeaderCell class="bg-body-tertiary">{{ t('鎖定類型') }}</CTableHeaderCell>
                    <CTableHeaderCell class="bg-body-tertiary">{{ t('對象（帳號／來源 IP）') }}</CTableHeaderCell>
                    <CTableHeaderCell class="bg-body-tertiary">{{ t('失敗次數') }}</CTableHeaderCell>
                    <CTableHeaderCell class="bg-body-tertiary">{{ t('解鎖時間') }}</CTableHeaderCell>
                    <CTableHeaderCell class="bg-body-tertiary text-end">{{ t('操作') }}</CTableHeaderCell>
                  </CTableRow>
                </CTableHead>
                <CTableBody>
                  <CTableRow v-for="lockout in data.threats.activeLockouts" :key="lockout.bucketKey">
                    <CTableDataCell>
                      <CBadge color="danger">
                        {{ lockout.kind === 'account' ? t('帳號鎖定') : t('來源 IP 鎖定') }}
                      </CBadge>
                    </CTableDataCell>
                    <CTableDataCell class="font-monospace fw-semibold">{{ lockout.target }}</CTableDataCell>
                    <CTableDataCell>{{ lockout.failures }} / {{ lockout.limit }}</CTableDataCell>
                    <CTableDataCell class="small text-body-secondary">
                      {{ dateTime(lockout.lockedUntil) }}
                      （{{ t('剩餘 {seconds} 秒', { seconds: lockout.remainingSeconds }) }}）
                    </CTableDataCell>
                    <CTableDataCell class="text-end">
                      <CButton
                        color="success"
                        variant="outline"
                        size="sm"
                        @click="unlockTarget(lockout.bucketKey, lockout.target)"
                      >
                        <CIcon icon="cil-lock-unlocked" class="me-1" />{{ t('解除鎖定') }}
                      </CButton>
                    </CTableDataCell>
                  </CTableRow>
                  <CTableRow v-if="data.threats.activeLockouts.length === 0">
                    <CTableDataCell colspan="5" class="text-center text-body-secondary py-3">
                      {{ t('目前無生效中的登入鎖定紀錄。') }}
                    </CTableDataCell>
                  </CTableRow>
                </CTableBody>
              </CTable>
            </div>
          </div>

          <div>
            <div class="d-flex flex-wrap align-items-center justify-content-between gap-2 mb-3">
              <div>
                <div class="fw-semibold">{{ t('近期安全告警與存取拒絕事件') }}</div>
                <div class="small text-body-secondary">
                  {{ t('自動彙整登入失敗、鎖定、權限拒絕、機密揭露與安全政策異動紀錄。') }}
                </div>
              </div>
              <CFormSelect v-model="threatKindFilter" size="sm" style="width: 12rem">
                <option value="all">{{ t('所有安全事件') }}</option>
                <option value="locked-out">{{ t('登入鎖定') }}</option>
                <option value="sign-in-failed">{{ t('登入／OTP 失敗') }}</option>
                <option value="denied">{{ t('權限與 IP 拒絕') }}</option>
                <option value="reveal">{{ t('顯示機密值') }}</option>
                <option value="policy">{{ t('安全與權限異動') }}</option>
              </CFormSelect>
            </div>

            <div class="table-responsive">
              <CTable align="middle" bordered class="mb-0">
                <CTableHead class="text-nowrap">
                  <CTableRow>
                    <CTableHeaderCell class="bg-body-tertiary">{{ t('時間') }}</CTableHeaderCell>
                    <CTableHeaderCell class="bg-body-tertiary">{{ t('動作') }}</CTableHeaderCell>
                    <CTableHeaderCell class="bg-body-tertiary">{{ t('結果') }}</CTableHeaderCell>
                    <CTableHeaderCell class="bg-body-tertiary">{{ t('操作者') }}</CTableHeaderCell>
                    <CTableHeaderCell class="bg-body-tertiary">{{ t('來源位址') }}</CTableHeaderCell>
                    <CTableHeaderCell class="bg-body-tertiary">{{ t('說明') }}</CTableHeaderCell>
                  </CTableRow>
                </CTableHead>
                <CTableBody>
                  <CTableRow v-for="entry in threatPage.paged" :key="entry.id">
                    <CTableDataCell class="text-nowrap text-body-secondary">{{ dateTime(entry.occurredAt) }}</CTableDataCell>
                    <CTableDataCell>{{ t(actionLabels[entry.action] ?? entry.action) }}</CTableDataCell>
                    <CTableDataCell>
                      <CBadge
                        :color="
                          entry.outcome === 'success'
                            ? 'success'
                            : entry.outcome === 'denied'
                              ? 'warning'
                              : 'danger'
                        "
                        :text-color="entry.outcome === 'denied' ? 'dark' : undefined"
                      >
                        {{ t(outcomeLabels[entry.outcome] ?? entry.outcome) }}
                      </CBadge>
                    </CTableDataCell>
                    <CTableDataCell class="fw-semibold">{{ entry.actor }}</CTableDataCell>
                    <CTableDataCell class="font-monospace small">{{ entry.remoteAddress ?? '—' }}</CTableDataCell>
                    <CTableDataCell class="text-break">{{ entry.summary }}</CTableDataCell>
                  </CTableRow>
                  <CTableRow v-if="threatPage.paged.length === 0">
                    <CTableDataCell colspan="6" class="text-center text-body-secondary py-4">
                      {{ t('目前無符合條件的安全事件。') }}
                    </CTableDataCell>
                  </CTableRow>
                </CTableBody>
              </CTable>
            </div>

            <TablePagination
              plain
              :page="threatPage.page"
              :page-size="threatPage.pageSize"
              :total="threatPage.total"
              :pages="threatPage.pages"
              @update:page="threatPage.setPage"
              @update:page-size="threatPage.setPageSize"
            />
          </div>
        </template>

        <template v-else-if="activeTab === 'policies' && form">
          <CRow class="g-3">
            <CCol :xs="12">
              <CFormSwitch
                id="sec-require-signin"
                v-model="form.requireSignIn"
                :disabled="auth.users.length === 0"
                :label="t('啟用管理端登入')"
              />
              <CFormText v-if="auth.users.length === 0">{{ t('啟用登入前，請先建立一個管理帳號。') }}</CFormText>
              <CFormSwitch
                id="sec-require-otp"
                v-model="form.requireOtp"
                class="mt-2"
                :disabled="!form.requireSignIn"
                :label="t('要求所有帳號啟用一次性密碼')"
              />
              <CFormText>{{ t('開啟後，尚未設定的帳號登入後仍可使用，但無法自行停用一次性密碼。') }}</CFormText>
              <CFormSwitch id="sec-audit-log" v-model="form.auditLog" class="mt-2" :label="t('記錄操作稽核紀錄')" />
              <CFormText>{{ t('關閉或開啟稽核本身一定會留下一筆紀錄，並發出安全告警。') }}</CFormText>
              <CFormLabel for="sec-audit-retention" class="mt-3">{{ t('稽核保留天數') }}</CFormLabel>
              <CFormInput
                id="sec-audit-retention"
                v-model.number="form.auditRetentionDays"
                type="number"
                min="30"
                max="3650"
                :invalid="Boolean(errors.auditRetentionDays)"
              />
              <CFormText>{{ t('至少 30 天，與建置紀錄的保留天數分開計算。') }}</CFormText>
              <div class="d-flex align-items-center gap-2 mt-2">
                <CFormSwitch id="sec-mask-secrets" v-model="form.maskSecrets" :label="t('在建置輸出中遮蔽機密值')" />
                <CBadge v-if="!form.maskSecrets" color="warning" text-color="dark">{{ t('機密遮蔽已關閉') }}</CBadge>
              </div>
              <CFormText>{{ t('關閉後，建置輸出會原樣顯示機密變數的內容。') }}</CFormText>
              <CFormSwitch
                id="sec-allow-operator-ai"
                v-model="form.allowOperatorAi"
                class="mt-2"
                :label="t('允許操作者使用 AI 功能')"
              />
              <CFormText>
                {{ t('關閉後，只有管理員能使用 AI 審閱腳本與分析建置；這些功能會把腳本與建置輸出送到 AI 供應商。') }}
              </CFormText>
            </CCol>

            <CCol :md="6">
              <CFormLabel for="sec-cors">{{ t('允許的 CORS 來源') }}</CFormLabel>
              <CFormTextarea id="sec-cors" v-model="form.corsOrigins" class="font-monospace" rows="3" />
              <CFormText>{{ t('一行一個來源，儲存後立即生效。') }}</CFormText>
            </CCol>

            <CCol :md="6">
              <CFormLabel for="sec-mcp-allowed-addresses">{{ t('MCP 允許的來源位址') }}</CFormLabel>
              <CFormTextarea
                id="sec-mcp-allowed-addresses"
                v-model="form.mcpAllowedAddresses"
                class="font-monospace"
                rows="3"
                placeholder="127.0.0.1/32&#10;::1/128&#10;203.0.113.0/24"
              />
              <CFormText>
                {{ t('每行一個 IP 或 CIDR；空白＝不限制。設定後只有列出的位址能連 /mcp，本機也要列出 127.0.0.1/32 與 ::1/128 才連得到。') }}
              </CFormText>
            </CCol>

            <CCol :md="6">
              <CFormLabel for="sec-webhook-secret">{{ t('Webhook 密鑰') }}</CFormLabel>
              <CFormInput
                id="sec-webhook-secret"
                v-model="form.webhookSecret"
                class="font-monospace"
                type="password"
                autocomplete="new-password"
              />
              <CFormText>{{ t('設定後，GitHub 需帶 X-Hub-Signature-256，其他來源可用 X-Walis-Token。') }}</CFormText>

              <CFormLabel for="sec-webhook-url" class="mt-3">{{ t('推送 webhook 網址') }}</CFormLabel>
              <CInputGroup>
                <CFormInput id="sec-webhook-url" class="font-monospace" :model-value="webhookUrl" readonly />
                <CButton color="secondary" variant="outline" @click="copyWebhookUrl">{{ t('複製') }}</CButton>
              </CInputGroup>
              <CFormText>{{ t('把這個網址填到 Git 服務的 webhook 設定，推送時會觸發對應分支的任務。') }}</CFormText>
            </CCol>

            <CCol :md="6">
              <CFormLabel for="sec-notification-signing-secret">{{ t('通知簽章密鑰') }}</CFormLabel>
              <CFormInput
                id="sec-notification-signing-secret"
                v-model="form.notificationSigningSecret"
                class="font-monospace"
                type="password"
                autocomplete="new-password"
              />
              <CFormText>
                {{
                  t('設定後（32 到 1,024 個字元），webhook 通知會帶 Walis-Signature: t=<unix 秒>,v1=<hex> 與 Walis-Event-Id 標頭；v1 是以此密鑰對「t.原始內容」計算的 HMAC-SHA256。留空則不簽章。')
                }}
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

          <div class="d-flex flex-wrap align-items-center gap-2 mb-3">
            <div class="me-auto">
              <div class="fw-semibold">{{ t('完整操作稽核日誌') }}</div>
              <div class="small text-body-secondary">{{ t('稽核紀錄會依「稽核保留天數」自動清除。') }}</div>
            </div>
            <a class="btn btn-outline-secondary btn-sm" :href="auditExportUrl" download>
              <CIcon icon="cil-cloud-download" class="me-1" />{{ t('匯出 CSV') }}
            </a>
            <CButton color="secondary" variant="outline" size="sm" :disabled="auditLoading" @click="loadAudit">
              <CIcon icon="cil-reload" class="me-1" />{{ t('重新整理') }}
            </CButton>
          </div>

          <CRow class="g-2 mb-3 align-items-end">
            <CCol :md="2">
              <CFormLabel for="sec-audit-actor">{{ t('操作者') }}</CFormLabel>
              <CFormInput id="sec-audit-actor" v-model="auditFilter.actor" @keyup.enter="applyAuditFilter" />
            </CCol>
            <CCol :md="2">
              <CFormLabel for="sec-audit-entity-type">{{ t('對象') }}</CFormLabel>
              <CFormSelect id="sec-audit-entity-type" v-model="auditFilter.entityType">
                <option value="">{{ t('全部') }}</option>
                <option v-for="(label, key) in entityLabels" :key="key" :value="key">{{ t(label) }}</option>
              </CFormSelect>
            </CCol>
            <CCol :md="2">
              <CFormLabel for="sec-audit-outcome">{{ t('結果') }}</CFormLabel>
              <CFormSelect id="sec-audit-outcome" v-model="auditFilter.outcome">
                <option value="">{{ t('全部') }}</option>
                <option v-for="(label, key) in outcomeLabels" :key="key" :value="key">{{ t(label) }}</option>
              </CFormSelect>
            </CCol>
            <CCol :md="2">
              <CFormLabel for="sec-audit-from">{{ t('起') }}</CFormLabel>
              <CFormInput id="sec-audit-from" v-model="auditFilter.from" type="date" />
            </CCol>
            <CCol :md="2">
              <CFormLabel for="sec-audit-to">{{ t('迄') }}</CFormLabel>
              <CFormInput id="sec-audit-to" v-model="auditFilter.to" type="date" />
            </CCol>
            <CCol :md="2" class="d-flex gap-2">
              <CButton color="primary" size="sm" :disabled="auditLoading" @click="applyAuditFilter">
                {{ t('查詢') }}
              </CButton>
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
                    <CBadge
                      :color="
                        entry.outcome === 'success'
                          ? 'success'
                          : entry.outcome === 'denied'
                            ? 'warning'
                            : 'danger'
                      "
                      :text-color="entry.outcome === 'denied' ? 'dark' : undefined"
                    >
                      {{ t(outcomeLabels[entry.outcome] ?? entry.outcome) }}
                    </CBadge>
                  </CTableDataCell>
                  <CTableDataCell>
                    {{ t(entityLabels[entry.entityType] ?? entry.entityType) }}
                    <div v-if="entry.entityId" class="font-monospace small text-body-secondary text-break">
                      {{ entry.entityId }}
                    </div>
                  </CTableDataCell>
                  <CTableDataCell class="text-break">
                    {{ entry.summary }}
                    <div v-if="entry.requestId" class="small text-body-secondary font-monospace">
                      {{ entry.requestId }}
                    </div>
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
        v-if="activeTab === 'policies'"
        :page="auditPageNumber"
        :page-size="auditPageSize"
        :total="auditTotal"
        :pages="auditPages"
        :page-sizes="PAGE_SIZES"
        :summary="
          t('顯示第 {from}–{to} 筆，共 {total} 筆稽核紀錄', {
            from: auditRangeFrom,
            to: auditRangeTo,
            total: auditTotal
          })
        "
        @update:page="(value: number) => (auditPageNumber = value)"
        @update:page-size="(value: number) => (auditPageSize = value)"
      />
    </CCard>

    <PageActions v-if="activeTab === 'policies'">
      <span class="text-body-secondary small d-none d-md-inline">
        {{ t('設定會立即套用到排程、建置與通知。') }}
      </span>
      <CButton color="secondary" variant="outline" size="sm" :disabled="saving" @click="revertPolicies">
        {{ t('還原') }}
      </CButton>
      <CButton color="primary" size="sm" :disabled="saving" @click="savePolicies">
        <CSpinner v-if="saving" size="sm" class="me-2" />{{ t('儲存設定') }}
      </CButton>
    </PageActions>
  </div>

  <div v-else class="d-flex justify-content-center py-5">
    <CSpinner color="primary" />
  </div>
</template>
