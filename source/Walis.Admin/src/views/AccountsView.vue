<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import TablePagination from '../components/TablePagination.vue'
import { usePagedRows } from '../composables/usePagedRows'
import PageTabs from '../components/PageTabs.vue'
import PageActions from '../components/PageActions.vue'
import OtpSetupModal from '../components/OtpSetupModal.vue'
import { ApiError, api } from '../services/api'
import { useFormat } from '../composables/useFormat'
import { useAuthStore } from '../stores/auth'
import { useSettingsStore } from '../stores/settings'
import { useToastStore } from '../stores/toasts'
import type { ApiKey, UserRoleKind, WalisUser } from '../types'

const { t } = useI18n()

const tab = ref<'accounts' | 'keys' | 'otp' | 'password'>('accounts')
const tabs = computed(() => [
  { key: 'accounts', label: '帳號管理', icon: 'cil-user', badge: auth.users.length },
  { key: 'keys', label: 'API 金鑰', icon: 'cil-lock-locked' },
  { key: 'otp', label: '我的一次性密碼', icon: 'cil-lock-locked' },
  { key: 'password', label: '變更我的密碼', icon: 'cil-user' }
])
const auth = useAuthStore()
const settings = useSettingsStore()
const toasts = useToastStore()
const { relative, dateTime } = useFormat()

const createDialog = ref(false)
const editing = ref<WalisUser | null>(null)
const passwordTarget = ref<WalisUser | null>(null)
const pendingDelete = ref<WalisUser | null>(null)
const otpDialog = ref(false)
const disableOtpDialog = ref(false)
const recoveryDialog = ref(false)
const recoveryCodes = ref<string[]>([])
const errors = ref<Record<string, string[]>>({})
const busy = ref(false)

const createForm = reactive({ username: '', displayName: '', password: '', role: 'viewer' as UserRoleKind })
const editForm = reactive({ displayName: '', role: 'viewer' as UserRoleKind, isDisabled: false })
const passwordForm = reactive({ newPassword: '' })
const ownPasswordForm = reactive({ currentPassword: '', newPassword: '' })

const roles: { value: UserRoleKind; label: string; hint: string }[] = [
  { value: 'admin', label: '管理員', hint: '可以管理帳號、系統設定與所有資源。' },
  { value: 'operator', label: '操作者', hint: '可以管理任務、執行建置與環境變數。' },
  { value: 'viewer', label: '檢視者', hint: '只能瀏覽，不能做任何變更。' }
]

const roleLabels = computed(() => Object.fromEntries(roles.map((role) => [role.value, role.label])));

const me = computed(() => auth.users.find((user) => user.username === auth.user?.username) ?? null)
const canManage = computed(() => auth.isAdmin)

function openCreate(): void {
  createForm.username = ''
  createForm.displayName = ''
  createForm.password = ''
  createForm.role = 'viewer'
  errors.value = {}
  createDialog.value = true
}

function closeCreate(): void {
  createDialog.value = false
  createForm.password = ''
  errors.value = {}
}

function openPasswordReset(user: WalisUser): void {
  passwordForm.newPassword = ''
  errors.value = {}
  passwordTarget.value = user
}

function closePasswordReset(): void {
  passwordTarget.value = null
  passwordForm.newPassword = ''
  errors.value = {}
}

function openDisableOtp(): void {
  ownPasswordForm.currentPassword = ''
  errors.value = {}
  disableOtpDialog.value = true
}

function closeDisableOtp(): void {
  disableOtpDialog.value = false
  ownPasswordForm.currentPassword = ''
  errors.value = {}
}

function openRecoveryCodes(): void {
  ownPasswordForm.currentPassword = ''
  recoveryCodes.value = []
  errors.value = {}
  recoveryDialog.value = true
}

function closeRecoveryCodes(): void {
  recoveryDialog.value = false
  recoveryCodes.value = []
  ownPasswordForm.currentPassword = ''
  errors.value = {}
}

function openEdit(user: WalisUser): void {
  editing.value = user
  editForm.displayName = user.displayName
  editForm.role = user.role
  editForm.isDisabled = user.isDisabled
  errors.value = {}
}

function report(exception: unknown): void {
  if (exception instanceof ApiError) {
    errors.value = exception.errors
    toasts.error(exception.message)
  } else {
    toasts.error(exception instanceof Error ? exception.message : String(exception))
  }
}

async function create(): Promise<void> {
  errors.value = {}

  try {
    await auth.createUser(createForm.username.trim(), createForm.displayName.trim() || createForm.username.trim(), createForm.password, createForm.role)
    closeCreate()
    toasts.success(t('帳號已建立'))
  } catch (exception) {
    report(exception)
  }
}

async function save(): Promise<void> {
  if (!editing.value) {
    return
  }

  errors.value = {}

  try {
    await auth.updateUser(editing.value.id, editForm.displayName.trim(), editForm.role, editForm.isDisabled)
    editing.value = null
    toasts.success(t('帳號已更新'))
  } catch (exception) {
    report(exception)
  }
}

async function resetPassword(): Promise<void> {
  if (!passwordTarget.value) {
    return
  }

  errors.value = {}

  try {
    await auth.resetPassword(passwordTarget.value.id, passwordForm.newPassword)
    toasts.success(t('已重設 {name} 的密碼', { name: passwordTarget.value.username }))
    closePasswordReset()
  } catch (exception) {
    report(exception)
  }
}

async function resetOtp(user: WalisUser): Promise<void> {
  try {
    await auth.resetOtp(user.id)
    toasts.push(t('已清除 {name} 的一次性密碼', { name: user.username }), 'secondary')
  } catch (exception) {
    report(exception)
  }
}

async function remove(): Promise<void> {
  const user = pendingDelete.value

  if (!user) {
    return
  }

  try {
    await auth.removeUser(user.id)
    toasts.error(t('已刪除帳號 {name}', { name: user.username }))
  } catch (exception) {
    report(exception)
  } finally {
    pendingDelete.value = null
  }
}

async function changeOwnPassword(): Promise<void> {
  busy.value = true
  errors.value = {}

  try {
    await api.changePassword(ownPasswordForm.currentPassword, ownPasswordForm.newPassword)
    ownPasswordForm.currentPassword = ''
    ownPasswordForm.newPassword = ''
    toasts.success(t('密碼已更新'))
  } catch (exception) {
    report(exception)
  } finally {
    busy.value = false
  }
}

async function disableOwnOtp(): Promise<void> {
  busy.value = true
  errors.value = {}

  try {
    await api.disableOtp(ownPasswordForm.currentPassword)
    closeDisableOtp()
    await Promise.all([auth.refresh(), auth.loadUsers()])
    toasts.push(t('已停用一次性密碼'), 'secondary')
  } catch (exception) {
    report(exception)
  } finally {
    busy.value = false
  }
}

async function regenerateRecoveryCodes(): Promise<void> {
  busy.value = true
  errors.value = {}

  try {
    const result = await api.regenerateRecoveryCodes(ownPasswordForm.currentPassword)
    recoveryCodes.value = result.recoveryCodes
    ownPasswordForm.currentPassword = ''
    toasts.success(t('已重新產生備援碼'))
  } catch (exception) {
    report(exception)
  } finally {
    busy.value = false
  }
}

async function closeOtpDialog(): Promise<void> {
  otpDialog.value = false
  await Promise.all([auth.refresh(), auth.loadUsers()])
}

const apiKeys = ref<ApiKey[]>([])
const keyForm = ref<{
  name: string
  role: UserRoleKind
  scope: 'mcp' | 'api'
  expiresAt: string
  runnerName: string
  allowedAddresses: string
} | null>(null)
const issuedToken = ref<string | null>(null)
const keySaving = ref(false)

async function loadApiKeys(): Promise<void> {
  apiKeys.value = await api.apiKeys().catch(() => [])
}

function openKeyForm(): void {
  issuedToken.value = null
  keyForm.value = { name: '', role: 'operator', scope: 'mcp', expiresAt: '', runnerName: '', allowedAddresses: '' }
}

async function createApiKey(): Promise<void> {
  if (!keyForm.value) {
    return
  }

  keySaving.value = true

  try {
    const result = await api.createApiKey({
      name: keyForm.value.name,
      role: keyForm.value.role,
      scope: keyForm.value.scope,
      expiresAt: keyForm.value.expiresAt ? new Date(keyForm.value.expiresAt).toISOString() : null,
      runnerName: keyForm.value.runnerName.trim() || null,
      allowedAddresses: keyForm.value.allowedAddresses.trim() || null
    })

    // The plaintext appears this once only, so it stays on screen until the user closes it.
    issuedToken.value = result.token
    keyForm.value = null
    await loadApiKeys()
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  } finally {
    keySaving.value = false
  }
}

async function toggleApiKey(key: ApiKey): Promise<void> {
  try {
    await api.setApiKeyDisabled(key.id, !key.isDisabled)
    await loadApiKeys()
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  }
}

async function removeApiKey(key: ApiKey): Promise<void> {
  try {
    await api.deleteApiKey(key.id)
    await loadApiKeys()
    toasts.info(t('已刪除金鑰 {name}', { name: key.name }))
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  }
}

async function copyToken(): Promise<void> {
  if (!issuedToken.value) {
    return
  }

  try {
    await navigator.clipboard.writeText(issuedToken.value)
    toasts.success(t('金鑰已複製到剪貼簿'))
  } catch {
    toasts.error(t('瀏覽器不允許存取剪貼簿。'))
  }
}

onMounted(async () => {
  await Promise.all([settings.load(), auth.refresh()])
  await auth.loadUsers().catch(() => undefined)
  await loadApiKeys()
})

onBeforeUnmount(() => {
  issuedToken.value = null
  recoveryCodes.value = []
  createForm.password = ''
  passwordForm.newPassword = ''
  ownPasswordForm.currentPassword = ''
  ownPasswordForm.newPassword = ''
})

const userPage = usePagedRows(() => auth.users)

const apiKeyPage = usePagedRows(() => apiKeys.value)
</script>

<template>
    <PageActions>
      <CButton v-if="canManage && tab === 'accounts'" color="primary" size="sm" @click="openCreate">
        <CIcon icon="cil-plus" class="me-1" />{{ t('新增帳號') }}
      </CButton>
      <CButton v-if="canManage && tab === 'keys'" color="primary" size="sm" @click="openKeyForm">
        <CIcon icon="cil-plus" class="me-1" />{{ t('建立金鑰') }}
      </CButton>
    </PageActions>

  <PageTabs v-model="tab" :tabs="tabs">
    <template #accounts>
    <CCardBody class="p-0">
      <div class="table-responsive">
        <CTable align="middle" class="mb-0">
          <CTableHead class="text-nowrap">
            <CTableRow>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('名稱') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('帳號') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('角色') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('狀態') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('一次性密碼') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('最後登入') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary text-end">{{ t('操作') }}</CTableHeaderCell>
            </CTableRow>
          </CTableHead>
          <CTableBody>
            <CTableRow v-for="user in userPage.paged" :key="user.id">
              <CTableDataCell class="fw-semibold text-nowrap">
                {{ user.displayName }}
                <CBadge v-if="user.username === auth.user?.username" color="primary" size="sm" class="ms-1">
                  {{ t('目前使用') }}
                </CBadge>
              </CTableDataCell>
              <CTableDataCell class="font-monospace text-body-secondary">{{ user.username }}</CTableDataCell>
              <CTableDataCell>
                <CBadge color="light" class="border text-body">{{ t(roleLabels[user.role]) }}</CBadge>
              </CTableDataCell>
              <CTableDataCell>
                <CBadge :text-bg-color="user.isDisabled ? 'secondary' : 'success'">
                  {{ t(user.isDisabled ? '已停用' : '啟用中') }}
                </CBadge>
              </CTableDataCell>
              <CTableDataCell>
                <CBadge :text-bg-color="user.hasOtp ? 'success' : 'warning'" :title="user.totpEnabledAt ? dateTime(user.totpEnabledAt) : ''">
                  {{ t(user.hasOtp ? '已啟用' : '未設定') }}
                </CBadge>
              </CTableDataCell>
              <CTableDataCell class="text-body-secondary">
                {{ user.lastLoginAt ? relative(user.lastLoginAt) : t('尚未登入') }}
              </CTableDataCell>
              <CTableDataCell class="text-end text-nowrap">
                <template v-if="canManage">
                  <CButton color="primary" variant="ghost" size="sm" :title="t('編輯')" @click="openEdit(user)">
                    <CIcon icon="cil-pencil" />
                  </CButton>
                  <CDropdown placement="bottom-end" class="d-inline-block" teleport>
                    <CDropdownToggle color="secondary" variant="ghost" size="sm" :caret="false" :title="t('更多操作')">
                      <CIcon icon="cil-options" />
                    </CDropdownToggle>
                    <CDropdownMenu>
                      <CDropdownItem component="button" @click="openPasswordReset(user)">{{ t('重設密碼') }}</CDropdownItem>
                      <CDropdownItem component="button" :disabled="!user.hasOtp" @click="resetOtp(user)">
                        {{ t('清除一次性密碼') }}
                      </CDropdownItem>
                      <CDropdownDivider />
                      <CDropdownItem
                        component="button"
                        class="text-danger"
                        :disabled="user.username === auth.user?.username"
                        @click="pendingDelete = user"
                      >
                        {{ t('刪除帳號') }}
                      </CDropdownItem>
                    </CDropdownMenu>
                  </CDropdown>
                </template>
                <span v-else class="text-body-secondary small">—</span>
              </CTableDataCell>
            </CTableRow>

            <CTableRow v-if="auth.users.length === 0">
              <CTableDataCell colspan="7" class="text-center text-body-secondary py-5">
                {{ t('尚未建立任何帳號。') }}
              </CTableDataCell>
            </CTableRow>
          </CTableBody>
        </CTable>
      </div>
    </CCardBody>
    <TablePagination
      :page="userPage.page"
      :page-size="userPage.pageSize"
      :total="userPage.total"
      :pages="userPage.pages"
      @update:page="userPage.setPage"
      @update:page-size="userPage.setPageSize"
    />

    <CCardFooter class="text-body-secondary small">
      {{ t('登入功能要在系統設定的安全分頁開啟後才會生效。') }}
    </CCardFooter>
    </template>
    <template #keys>
    <CCardBody>
      <CAlert v-if="issuedToken" color="success" class="d-flex flex-column gap-2">
        <div class="fw-semibold">{{ t('金鑰只會顯示這一次，請立刻保存。') }}</div>
        <CInputGroup>
          <CFormInput class="font-monospace" :model-value="issuedToken" type="password" autocomplete="off" readonly />
          <CButton color="secondary" variant="outline" @click="copyToken">{{ t('複製') }}</CButton>
          <CButton color="secondary" variant="outline" @click="issuedToken = null">{{ t('關閉') }}</CButton>
        </CInputGroup>
      </CAlert>

      <div v-if="keyForm" class="border rounded p-3 mb-3">
        <CRow class="g-3">
          <CCol :md="4">
            <CFormLabel for="key-name">{{ t('名稱') }}</CFormLabel>
            <CFormInput id="key-name" v-model="keyForm.name" :placeholder="t('例如：Claude 代理')" />
          </CCol>
          <CCol :md="2">
            <CFormLabel for="key-role">{{ t('角色') }}</CFormLabel>
            <CFormSelect id="key-role" v-model="keyForm.role">
              <option value="admin">admin</option>
              <option value="operator">operator</option>
              <option value="viewer">viewer</option>
            </CFormSelect>
          </CCol>
          <CCol :md="3">
            <CFormLabel for="key-scope">{{ t('用途範圍') }}</CFormLabel>
            <CFormSelect id="key-scope" v-model="keyForm.scope" :disabled="Boolean(keyForm.runnerName.trim())">
              <option value="mcp">mcp（{{ t('僅 AI 工具') }}）</option>
              <option value="api">api（{{ t('完整角色權限') }}）</option>
            </CFormSelect>
            <CFormText>{{ t('一般 AI 代理請保留 mcp；只有受信任的自動化程式才使用 api。') }}</CFormText>
          </CCol>
          <CCol :md="3">
            <CFormLabel for="key-expires">{{ t('到期時間') }}</CFormLabel>
            <CFormInput id="key-expires" v-model="keyForm.expiresAt" type="date" />
            <CFormText>{{ t('留空表示不過期。') }}</CFormText>
          </CCol>
          <CCol :md="12">
            <CFormLabel for="key-runner">{{ t('綁定執行器') }}</CFormLabel>
            <CFormInput id="key-runner" v-model="keyForm.runnerName" class="font-monospace" :placeholder="t('例如：builder-01')" />
            <CFormText>{{ t('填了名稱後，這把金鑰只能給該執行器認領與回報建置，不能存取其他 API；給 walis-runner 用的金鑰請一律綁定。') }}</CFormText>
          </CCol>
          <CCol :md="12">
            <CFormLabel for="key-allowed-addresses">{{ t('允許的來源位址') }}</CFormLabel>
            <CFormTextarea
              id="key-allowed-addresses"
              v-model="keyForm.allowedAddresses"
              class="font-monospace"
              rows="2"
              placeholder="203.0.113.10&#10;2001:db8::/64"
            />
            <CFormText>{{ t('選填。每行一個 IP 或 CIDR；填了之後，這把金鑰只能從列出的位址使用（反向代理後方要先設定 Security:TrustedProxies）。') }}</CFormText>
          </CCol>
          <CCol :xs="12" class="d-flex gap-2">
            <CButton color="primary" :disabled="keySaving || !keyForm.name" @click="createApiKey">
              <CSpinner v-if="keySaving" size="sm" class="me-2" />{{ t('建立') }}
            </CButton>
            <CButton color="secondary" variant="ghost" @click="keyForm = null">{{ t('取消') }}</CButton>
          </CCol>
        </CRow>
      </div>

      <div class="table-responsive">
        <CTable align="middle" bordered class="mb-0">
          <CTableHead class="text-nowrap">
            <CTableRow>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('名稱') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('前綴') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('角色') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('用途範圍') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('綁定執行器') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('允許的來源位址') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('最後使用') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('狀態') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary text-end">{{ t('操作') }}</CTableHeaderCell>
            </CTableRow>
          </CTableHead>
          <CTableBody>
            <CTableRow v-for="key in apiKeyPage.paged" :key="key.id">
              <CTableDataCell class="fw-semibold">{{ key.name }}</CTableDataCell>
              <CTableDataCell class="font-monospace small">walis_{{ key.prefix }}…</CTableDataCell>
              <CTableDataCell>{{ key.role }}</CTableDataCell>
              <CTableDataCell class="font-monospace small">{{ key.scope }}</CTableDataCell>
              <CTableDataCell class="font-monospace small">{{ key.runnerName ?? '—' }}</CTableDataCell>
              <CTableDataCell class="font-monospace small" style="white-space: pre-line">{{ key.allowedAddresses ?? '—' }}</CTableDataCell>
              <CTableDataCell class="text-body-secondary small">
                {{ key.lastUsedAt ? dateTime(key.lastUsedAt) : t('尚未使用') }}
              </CTableDataCell>
              <CTableDataCell>
                <CBadge :color="key.isDisabled ? 'secondary' : 'success'">
                  {{ key.isDisabled ? t('停用') : t('啟用') }}
                </CBadge>
              </CTableDataCell>
              <CTableDataCell class="text-end text-nowrap">
                <CButton color="secondary" variant="ghost" size="sm" @click="toggleApiKey(key)">
                  {{ key.isDisabled ? t('啟用') : t('停用') }}
                </CButton>
                <CButton color="danger" variant="ghost" size="sm" @click="removeApiKey(key)">
                  {{ t('刪除') }}
                </CButton>
              </CTableDataCell>
            </CTableRow>
            <CTableRow v-if="apiKeys.length === 0">
              <CTableDataCell colspan="9" class="text-center text-body-secondary py-4">
                {{ t('還沒有金鑰。金鑰帶在 Authorization: Bearer 標頭，適合讓腳本或 AI 代理呼叫 API。') }}
              </CTableDataCell>
            </CTableRow>
          </CTableBody>
        </CTable>
      </div>
    </CCardBody>
    <TablePagination
      :page="apiKeyPage.page"
      :page-size="apiKeyPage.pageSize"
      :total="apiKeyPage.total"
      :pages="apiKeyPage.pages"
      @update:page="apiKeyPage.setPage"
      @update:page-size="apiKeyPage.setPageSize"
    />
    </template>
    <template #otp>
    <CCardBody>
      <p class="text-body-secondary">
        {{ t('啟用後，登入時除了密碼還需要輸入驗證器產生的 6 位數驗證碼。') }}
      </p>

      <div class="d-flex align-items-center gap-3 mb-3">
        <CBadge :text-bg-color="me?.hasOtp ? 'success' : 'warning'">
          {{ t(me?.hasOtp ? '已啟用' : '未設定') }}
        </CBadge>
        <span v-if="me?.totpEnabledAt" class="small text-body-secondary">
          {{ dateTime(me.totpEnabledAt) }}
        </span>
      </div>

      <div v-if="!auth.user" class="text-body-secondary small">
        {{ t('要先登入才能設定自己的一次性密碼。') }}
      </div>

      <div v-else class="d-flex flex-wrap gap-2">
        <CButton v-if="!me?.hasOtp" color="primary" size="sm" @click="otpDialog = true">
          <CIcon icon="cil-lock-locked" class="me-1" />{{ t('設定一次性密碼') }}
        </CButton>
        <template v-else>
          <CButton color="secondary" variant="outline" size="sm" @click="openRecoveryCodes">
            {{ t('重新產生備援碼') }}
          </CButton>
          <CButton
            color="danger"
            variant="outline"
            size="sm"
            :disabled="settings.settings?.requireOtp"
            @click="openDisableOtp"
          >
            {{ t('停用') }}
          </CButton>
        </template>
      </div>

      <CFormText v-if="settings.settings?.requireOtp" class="mt-2">
        {{ t('系統設定要求所有帳號都必須啟用一次性密碼，因此無法停用。') }}
      </CFormText>
    </CCardBody>
    </template>
    <template #password>
    <CCardBody>
      <div v-if="!auth.user" class="text-body-secondary small">
        {{ t('要先登入才能變更密碼。') }}
      </div>
      <CForm v-else novalidate @submit.prevent="changeOwnPassword">
        <div class="mb-3">
          <CFormLabel for="own-current">{{ t('目前的密碼') }}</CFormLabel>
          <CFormInput
            id="own-current"
            v-model="ownPasswordForm.currentPassword"
            type="password"
            autocomplete="current-password"
            :invalid="Boolean(errors.currentPassword)"
          />
          <CFormFeedback invalid>{{ errors.currentPassword?.[0] }}</CFormFeedback>
        </div>
        <div class="mb-3">
          <CFormLabel for="own-new">{{ t('新密碼') }}</CFormLabel>
          <CFormInput
            id="own-new"
            v-model="ownPasswordForm.newPassword"
            type="password"
            autocomplete="new-password"
            :invalid="Boolean(errors.newPassword)"
          />
          <CFormFeedback invalid>{{ errors.newPassword?.[0] }}</CFormFeedback>
          <CFormText>{{ t('密碼至少要 12 個字元。') }}</CFormText>
        </div>
        <CButton color="primary" type="submit" :disabled="busy">
          <CSpinner v-if="busy" size="sm" class="me-2" />{{ t('更新密碼') }}
        </CButton>
      </CForm>
    </CCardBody>
    </template>
  </PageTabs>


  <OtpSetupModal :visible="otpDialog" @close="closeOtpDialog" />

  <CModal :visible="createDialog" @close="closeCreate">
    <CModalHeader><CModalTitle>{{ t('新增帳號') }}</CModalTitle></CModalHeader>
    <CModalBody>
      <CRow class="g-3">
        <CCol :xs="12">
          <CFormLabel for="account-username">{{ t('帳號') }}</CFormLabel>
          <CFormInput id="account-username" v-model="createForm.username" :invalid="Boolean(errors.username)" />
          <CFormFeedback invalid>{{ errors.username?.[0] }}</CFormFeedback>
        </CCol>
        <CCol :xs="12">
          <CFormLabel for="account-display">{{ t('顯示名稱') }}</CFormLabel>
          <CFormInput id="account-display" v-model="createForm.displayName" />
        </CCol>
        <CCol :xs="12">
          <CFormLabel for="account-password">{{ t('密碼') }}</CFormLabel>
          <CFormInput
            id="account-password"
            v-model="createForm.password"
            type="password"
            autocomplete="new-password"
            :invalid="Boolean(errors.password)"
          />
          <CFormFeedback invalid>{{ errors.password?.[0] }}</CFormFeedback>
        </CCol>
        <CCol :xs="12">
          <CFormLabel for="account-role">{{ t('角色') }}</CFormLabel>
          <CFormSelect id="account-role" v-model="createForm.role">
            <option v-for="role in roles" :key="role.value" :value="role.value">{{ t(role.label) }}</option>
          </CFormSelect>
          <CFormText>{{ t(roles.find((role) => role.value === createForm.role)!.hint) }}</CFormText>
        </CCol>
      </CRow>
    </CModalBody>
    <CModalFooter>
      <CButton color="secondary" variant="outline" @click="closeCreate">{{ t('取消') }}</CButton>
      <CButton color="primary" @click="create">{{ t('建立') }}</CButton>
    </CModalFooter>
  </CModal>

  <CModal :visible="editing !== null" @close="editing = null">
    <CModalHeader><CModalTitle>{{ t('編輯帳號') }}</CModalTitle></CModalHeader>
    <CModalBody>
      <CRow class="g-3">
        <CCol :xs="12">
          <CFormLabel for="edit-display">{{ t('顯示名稱') }}</CFormLabel>
          <CFormInput id="edit-display" v-model="editForm.displayName" :invalid="Boolean(errors.displayName)" />
          <CFormFeedback invalid>{{ errors.displayName?.[0] }}</CFormFeedback>
        </CCol>
        <CCol :xs="12">
          <CFormLabel for="edit-role">{{ t('角色') }}</CFormLabel>
          <CFormSelect id="edit-role" v-model="editForm.role">
            <option v-for="role in roles" :key="role.value" :value="role.value">{{ t(role.label) }}</option>
          </CFormSelect>
          <CFormText>{{ t(roles.find((role) => role.value === editForm.role)!.hint) }}</CFormText>
        </CCol>
        <CCol :xs="12">
          <CFormSwitch id="edit-disabled" v-model="editForm.isDisabled" :label="t('停用這個帳號')" />
          <CFormText>{{ t('停用後無法登入，既有的登入狀態也會立即失效。') }}</CFormText>
        </CCol>
      </CRow>
    </CModalBody>
    <CModalFooter>
      <CButton color="secondary" variant="outline" @click="editing = null">{{ t('取消') }}</CButton>
      <CButton color="primary" @click="save">{{ t('儲存') }}</CButton>
    </CModalFooter>
  </CModal>

  <CModal :visible="passwordTarget !== null" @close="closePasswordReset">
    <CModalHeader><CModalTitle>{{ t('重設密碼') }}</CModalTitle></CModalHeader>
    <CModalBody>
      <p class="text-body-secondary">
        {{ t('將直接把 {name} 的密碼換成新密碼，對方下次登入請使用新密碼。', { name: passwordTarget?.username ?? '' }) }}
      </p>
      <CFormLabel for="reset-password">{{ t('新密碼') }}</CFormLabel>
      <CFormInput
        id="reset-password"
        v-model="passwordForm.newPassword"
        type="password"
        autocomplete="new-password"
        :invalid="Boolean(errors.newPassword)"
      />
      <CFormFeedback invalid>{{ errors.newPassword?.[0] }}</CFormFeedback>
    </CModalBody>
    <CModalFooter>
      <CButton color="secondary" variant="outline" @click="closePasswordReset">{{ t('取消') }}</CButton>
      <CButton color="primary" @click="resetPassword">{{ t('重設') }}</CButton>
    </CModalFooter>
  </CModal>

  <CModal :visible="disableOtpDialog" @close="closeDisableOtp">
    <CModalHeader><CModalTitle>{{ t('停用一次性密碼') }}</CModalTitle></CModalHeader>
    <CModalBody>
      <CFormLabel for="disable-otp-password">{{ t('目前的密碼') }}</CFormLabel>
      <CFormInput
        id="disable-otp-password"
        v-model="ownPasswordForm.currentPassword"
        type="password"
        autocomplete="current-password"
        :invalid="Boolean(errors.currentPassword)"
      />
      <CFormFeedback invalid>{{ errors.currentPassword?.[0] }}</CFormFeedback>
    </CModalBody>
    <CModalFooter>
      <CButton color="secondary" variant="outline" @click="closeDisableOtp">{{ t('取消') }}</CButton>
      <CButton color="danger" :disabled="busy" @click="disableOwnOtp">{{ t('停用') }}</CButton>
    </CModalFooter>
  </CModal>

  <CModal :visible="recoveryDialog" @close="closeRecoveryCodes">
    <CModalHeader><CModalTitle>{{ t('重新產生備援碼') }}</CModalTitle></CModalHeader>
    <CModalBody>
      <template v-if="recoveryCodes.length">
        <p class="text-body-secondary">{{ t('舊的備援碼已失效，請改用下面這組。') }}</p>
        <pre class="bg-body-tertiary border rounded p-3 mb-0"><code>{{ recoveryCodes.join('\n') }}</code></pre>
      </template>
      <template v-else>
        <CFormLabel for="recovery-password">{{ t('目前的密碼') }}</CFormLabel>
        <CFormInput
          id="recovery-password"
          v-model="ownPasswordForm.currentPassword"
          type="password"
          autocomplete="current-password"
          :invalid="Boolean(errors.currentPassword)"
        />
        <CFormFeedback invalid>{{ errors.currentPassword?.[0] }}</CFormFeedback>
      </template>
    </CModalBody>
    <CModalFooter>
      <CButton color="secondary" variant="outline" @click="closeRecoveryCodes">
        {{ t('關閉') }}
      </CButton>
      <CButton v-if="!recoveryCodes.length" color="primary" :disabled="busy" @click="regenerateRecoveryCodes">
        {{ t('產生') }}
      </CButton>
    </CModalFooter>
  </CModal>

  <CModal :visible="pendingDelete !== null" @close="pendingDelete = null">
    <CModalHeader><CModalTitle>{{ t('刪除帳號') }}</CModalTitle></CModalHeader>
    <CModalBody>{{ t('確定要刪除帳號「{name}」嗎？', { name: pendingDelete?.username ?? '' }) }}</CModalBody>
    <CModalFooter>
      <CButton color="secondary" variant="outline" @click="pendingDelete = null">{{ t('取消') }}</CButton>
      <CButton color="danger" @click="remove">{{ t('刪除') }}</CButton>
    </CModalFooter>
  </CModal>
</template>
