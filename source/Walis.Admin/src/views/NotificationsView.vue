<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import TablePagination from '../components/TablePagination.vue'
import { usePagedRows } from '../composables/usePagedRows'
import PageTabs from '../components/PageTabs.vue'
import PageActions from '../components/PageActions.vue'
import { ApiError } from '../services/api'
import { useFormat } from '../composables/useFormat'
import { useNotificationsStore } from '../stores/notifications'
import { useSettingsStore } from '../stores/settings'
import { useToastStore } from '../stores/toasts'
import type { NotificationChannel, WalisSettings } from '../types'

const { t } = useI18n()

    const tab = ref<'channels' | 'events' | 'log'>('channels')
    const tabs = [
      { key: 'channels', label: '通知管道', icon: 'cil-bell' },
      { key: 'events', label: '觸發事件', icon: 'cil-settings' },
      { key: 'log', label: '通知紀錄', icon: 'cil-list-rich' }
    ]
    const notifications = useNotificationsStore()
const settings = useSettingsStore()
const toasts = useToastStore()
const { dateTime } = useFormat()

const drafts = ref<Record<string, NotificationChannel>>({})
const saving = ref<string | null>(null)
const form = ref<WalisSettings | null>(null)

const channelIcons: Record<string, string> = {
  email: 'cil-envelope-open',
  slack: 'cil-share-alt',
  webhook: 'cil-code'
}

const channelHints: Record<string, string> = {
  email: '以純文字信件寄送建置結果摘要。',
  slack: '透過 Incoming Webhook 發送到指定頻道。',
  webhook: '以 POST 傳送 JSON，方便串接內部系統。'
}

const channelPlaceholders: Record<string, string> = {
  email: 'team@example.com',
  slack: 'https://hooks.slack.com/services/...',
  webhook: 'https://example.com/hooks/walis'
}

const events = computed(() => [
  { key: 'notifyOnFailure' as const, label: '建置失敗', hint: '任何任務建置失敗時立即發送。' },
  { key: 'notifyOnRecovery' as const, label: '建置恢復', hint: '前一次失敗、本次成功時發送。' },
  { key: 'notifyOnSuccess' as const, label: '每次建置成功', hint: '建議只在關鍵任務開啟，避免通知過多。' },
  { key: 'notifyOnTimeout' as const, label: '建置逾時', hint: '超過任務設定的逾時時間而被中止。' },
  { key: 'notifyOnRunnerOffline' as const, label: '執行器離線', hint: '執行器超過 5 分鐘沒有回報心跳。' },
  {
    key: 'notifyOnSecurityAlert' as const,
    label: '安全告警',
    hint: '登入鎖定、稽核紀錄被關閉、新增管理員或帳號升為管理員時發送；不受靜音時段影響。'
  }
])

const smtpConfigured = computed(() => Boolean(form.value?.smtpHost && form.value?.smtpFrom))

watch(
  () => notifications.channels,
  (channels) => {
    drafts.value = Object.fromEntries(channels.map((channel) => [channel.id, { ...channel }]))
  },
  { immediate: true, deep: true }
)

watch(
  () => settings.settings,
  (value) => {
    form.value = value ? { ...value } : null
  },
  { immediate: true }
)

async function saveChannel(id: string): Promise<void> {
  const draft = drafts.value[id]

  if (!draft) {
    return
  }

  saving.value = id

  try {
    await notifications.update(draft)
    toasts.success(t('{name} 的設定已儲存', { name: t(draft.name) }))
  } catch (error) {
    toasts.error(error instanceof ApiError ? error.message : String(error))
  } finally {
    saving.value = null
  }
}

async function test(channel: NotificationChannel): Promise<void> {
  try {
    const entry = await notifications.test(channel.id)

    if (entry.succeeded) {
      toasts.success(t('已送出測試通知'))
    } else {
      toasts.error(entry.detail ?? t('測試通知發送失敗'))
    }
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  }
}

async function saveSettings(): Promise<void> {
  if (!form.value) {
    return
  }

  try {
    await settings.save(form.value)
    toasts.success(t('通知設定已儲存'))
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  }
}

onMounted(() => Promise.all([notifications.load(), settings.load()]))

const logPage = usePagedRows(() => notifications.log)
</script>

<template>
    <PageActions>
      <CButton color="secondary" variant="outline" size="sm" @click="notifications.load()">
        <CIcon icon="cil-reload" class="me-1" />{{ t('重新整理') }}
      </CButton>
    </PageActions>

      <PageTabs v-model="tab" :tabs="tabs">
        <template #channels>
            <CCardBody>
          <div v-for="channel in notifications.channels" :key="channel.id" class="border rounded p-3 mb-3">
            <div class="d-flex align-items-center mb-3">
              <CAvatar color="secondary" size="md" class="me-3">
                <CIcon :icon="channelIcons[channel.kind]" size="lg" />
              </CAvatar>
              <div class="me-auto">
                <div class="fw-semibold">{{ t(channel.name) }}</div>
                <div class="small text-body-secondary">{{ t(channelHints[channel.kind]) }}</div>
              </div>
              <CFormSwitch
                v-if="drafts[channel.id]"
                v-model="drafts[channel.id].isEnabled"
                class="mb-0 ms-3"
                :aria-label="t('啟用 {name}', { name: t(channel.name) })"
              />
            </div>

            <CInputGroup v-if="drafts[channel.id]">
              <CFormInput
                v-model="drafts[channel.id].target"
                :placeholder="channelPlaceholders[channel.kind]"
                class="font-monospace"
                :title="channel.targetMasked ? t('網址只顯示主機；保留原樣代表不變，要更換時整個貼上新網址。') : undefined"
                @focus="channel.targetMasked && drafts[channel.id].target === channel.target && (drafts[channel.id].target = '')"
                @blur="channel.targetMasked && !drafts[channel.id].target && (drafts[channel.id].target = channel.target)"
              />
              <CButton color="primary" :disabled="saving === channel.id" @click="saveChannel(channel.id)">
                <CSpinner v-if="saving === channel.id" size="sm" class="me-1" />
                {{ t('儲存') }}
              </CButton>
              <CButton color="secondary" variant="outline" :disabled="!channel.target" @click="test(channel)">
                {{ t('測試') }}
              </CButton>
            </CInputGroup>

            <CAlert
              v-if="channel.kind === 'email' && drafts[channel.id]?.isEnabled && !smtpConfigured"
              color="warning"
              class="mt-3 mb-0 py-2 small"
            >
              {{ t('尚未設定 SMTP 主機或寄件人，通知只會留下紀錄不會實際寄出。') }}
            </CAlert>
          </div>
        </CCardBody>
    </template>
        <template #events>
            <CCardBody v-if="form">
          <div v-for="item in events" :key="item.key" class="form-check mb-3">
            <CFormCheck :id="`event-${item.key}`" v-model="form[item.key]" :label="t(item.label)" />
            <div class="small text-body-secondary ms-4">{{ t(item.hint) }}</div>
          </div>

          <hr />

          <CFormLabel>{{ t('靜音時段') }}</CFormLabel>
          <CInputGroup>
            <CFormInput v-model="form.quietHoursStart" placeholder="23:00" />
            <CInputGroupText>—</CInputGroupText>
            <CFormInput v-model="form.quietHoursEnd" placeholder="08:00" />
          </CInputGroup>
          <CFormText>{{ t('靜音時段內僅發送失敗通知。') }}</CFormText>

          <hr />

          <CFormLabel>{{ t('SMTP 設定') }}</CFormLabel>
          <CRow class="g-2">
            <CCol :xs="8">
              <CFormInput v-model="form.smtpHost" :placeholder="t('主機')" />
            </CCol>
            <CCol :xs="4">
              <CFormInput v-model.number="form.smtpPort" type="number" :placeholder="t('連接埠')" />
            </CCol>
            <CCol :xs="12">
              <CFormInput v-model="form.smtpFrom" :placeholder="t('寄件人')" />
            </CCol>
            <CCol :xs="6">
              <CFormInput v-model="form.smtpUser" :placeholder="t('使用者名稱')" />
            </CCol>
            <CCol :xs="6">
              <CFormInput
                v-model="form.smtpPassword"
                type="password"
                autocomplete="new-password"
                :placeholder="t('密碼')"
              />
            </CCol>
            <CCol :xs="12">
              <CFormSwitch id="smtp-ssl" v-model="form.smtpUseSsl" :label="t('使用 SSL/TLS')" />
            </CCol>
          </CRow>
        </CCardBody>
        <CCardFooter class="d-flex justify-content-end">
          <CButton color="primary" @click="saveSettings">{{ t('儲存設定') }}</CButton>
        </CCardFooter>
    </template>
        <template #log>
        <CCardBody class="p-0">
      <div class="table-responsive">
        <CTable align="middle" class="mb-0">
          <CTableHead class="text-nowrap">
            <CTableRow>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('時間') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('事件') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('管道') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('對象') }}</CTableHeaderCell>
                  <CTableHeaderCell class="bg-body-tertiary">{{ t('詳情') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary text-end">{{ t('結果') }}</CTableHeaderCell>
            </CTableRow>
          </CTableHead>
          <CTableBody>
            <CTableRow v-for="entry in logPage.paged" :key="entry.id">
              <CTableDataCell class="text-body-secondary">{{ dateTime(entry.sentAt) }}</CTableDataCell>
              <CTableDataCell class="fw-semibold">{{ entry.event }}</CTableDataCell>
              <CTableDataCell>{{ entry.channelKind }}</CTableDataCell>
              <CTableDataCell class="text-body-secondary text-break">{{ entry.target }}</CTableDataCell>
              <CTableDataCell class="text-body-secondary small text-truncate" style="max-width: 20rem" :title="entry.detail ?? ''">
                {{ entry.detail || '—' }}
              </CTableDataCell>
              <CTableDataCell class="text-end">
                <CBadge :text-bg-color="entry.succeeded ? 'success' : 'danger'">
                  {{ t(entry.succeeded ? '已送出' : '失敗') }}
                </CBadge>
              </CTableDataCell>
            </CTableRow>

            <CTableRow v-if="notifications.log.length === 0">
              <CTableDataCell colspan="6" class="text-center text-body-secondary py-5">
                {{ t('尚未有通知紀錄。') }}
              </CTableDataCell>
            </CTableRow>
          </CTableBody>
        </CTable>
      </div>
    </CCardBody>
    <TablePagination
      :page="logPage.page"
      :page-size="logPage.pageSize"
      :total="logPage.total"
      :pages="logPage.pages"
      @update:page="logPage.setPage"
      @update:page-size="logPage.setPageSize"
    />
    </template>
      </PageTabs>
    </template>
