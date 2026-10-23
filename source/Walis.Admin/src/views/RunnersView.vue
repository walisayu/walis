<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import TablePagination from '../components/TablePagination.vue'
import { usePagedRows } from '../composables/usePagedRows'
import PageTabs from '../components/PageTabs.vue'
import PageActions from '../components/PageActions.vue'
import { ApiError } from '../services/api'
import { useFormat } from '../composables/useFormat'
import { api } from '../services/api'
import { useAuthStore } from '../stores/auth'
import { useRunnersStore } from '../stores/runners'
import { useSettingsStore } from '../stores/settings'
import { useToastStore } from '../stores/toasts'
import type { Runner, RunnerInput } from '../types'

const { t } = useI18n()

const tab = ref<'runners' | 'log'>('runners')
const tabs = [
  { key: 'runners', label: '執行器', icon: 'cil-terminal' },
  { key: 'log', label: '工作紀錄', icon: 'cil-list-rich' }
]
const runners = useRunnersStore()
const settings = useSettingsStore()
const auth = useAuthStore()
const toasts = useToastStore()
const { relative } = useFormat()

const editorVisible = ref(false)
const editing = ref<Runner | null>(null)
const pendingDelete = ref<Runner | null>(null)

// Deleting a runner also disables every API key bound to its name; the count is shown before confirming.
// The key list is admin-only, so non-admins get the generic wording without a number.
const pendingKeyCount = ref<number | null>(null)
const errors = ref<Record<string, string[]>>({})

const form = reactive<RunnerInput>({
  name: '',
  operatingSystem: '',
  version: '',
  labels: '',
  totalSlots: 2,
  isPaused: false,
  minimumRunRole: 'operator'
})

const statusColors: Record<string, string> = {
  Idle: 'success',
  Busy: 'warning',
  Paused: 'info',
  Offline: 'secondary'
}

const statusLabels: Record<string, string> = {
  Idle: '待命',
  Busy: '執行中',
  Paused: '已暫停',
  Offline: '離線'
}

const claimCommand = computed(() => {
  const base = settings.settings?.baseUrl ?? 'http://127.0.0.1:11126'
  return `walis-runner \\\n  --server ${base} \\\n  --name builder-01 \\\n  --labels linux,docker`
})

function openEditor(runner: Runner | null): void {
  editing.value = runner
  errors.value = {}

  Object.assign(form, {
    name: runner?.name ?? '',
    operatingSystem: runner?.operatingSystem ?? '',
    version: runner?.version ?? '',
    labels: runner?.labels ?? '',
    totalSlots: runner?.totalSlots ?? 2,
    isPaused: runner?.isPaused ?? false,
    minimumRunRole: runner?.minimumRunRole ?? 'operator'
  })

  editorVisible.value = true
}

async function save(): Promise<void> {
  errors.value = {}

  try {
    if (editing.value) {
      await runners.update(editing.value.id, { ...form })
      toasts.success(t('執行器已更新'))
    } else {
      await runners.create({ ...form })
      toasts.success(t('執行器已註冊'))
    }

    editorVisible.value = false
  } catch (error) {
    if (error instanceof ApiError) {
      errors.value = error.errors
      toasts.error(error.message)
    } else {
      toasts.error(error instanceof Error ? error.message : String(error))
    }
  }
}

async function togglePaused(runner: Runner): Promise<void> {
  try {
    await runners.setPaused(runner.id, !runner.isPaused)
    toasts.push(
      t(runner.isPaused ? '{name} 已恢復接單' : '已暫停 {name} 接受新工作', { name: runner.name }),
      runner.isPaused ? 'success' : 'secondary'
    )
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  }
}

async function confirmDelete(runner: Runner): Promise<void> {
  pendingDelete.value = runner
  pendingKeyCount.value = null

  if (!auth.isAdmin) {
    return
  }

  try {
    const keys = await api.apiKeys()
    pendingKeyCount.value = keys.filter(
      (key) => !key.isDisabled && key.runnerName?.toLowerCase() === runner.name.toLowerCase()
    ).length
  } catch {
    pendingKeyCount.value = null
  }
}

async function remove(): Promise<void> {
  const runner = pendingDelete.value

  if (!runner) {
    return
  }

  try {
    const disabled = await runners.remove(runner.id)
    toasts.error(
      disabled > 0
        ? t('已刪除執行器 {name}，並停用 {count} 把金鑰', { name: runner.name, count: disabled })
        : t('已刪除執行器 {name}', { name: runner.name })
    )
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  } finally {
    pendingDelete.value = null
  }
}

onMounted(() => Promise.all([runners.load(true), settings.load()]))

const runnerPage = usePagedRows(() => runners.items)
</script>

<template>
    <PageActions>
      <CButton color="primary" size="sm" @click="openEditor(null)">
        <CIcon icon="cil-plus" class="me-1" />{{ t('註冊執行器') }}
      </CButton>
    </PageActions>

  <PageTabs v-model="tab" :tabs="tabs">
    <template #runners>
      <CCardBody>
  <CRow class="g-4 mb-4">
    <CCol v-for="runner in runners.items" :key="runner.id" :md="6" :xl="4">
      <CCard class="h-100">
        <CCardBody>
          <div class="d-flex align-items-center mb-3">
            <CAvatar color="secondary" size="md" class="me-3"><CIcon icon="cil-terminal" size="lg" /></CAvatar>
            <div class="me-auto">
              <div class="fw-semibold">
                {{ runner.name }}
                <CBadge v-if="runner.isLocal" color="primary" class="ms-1" size="sm">{{ t('內建') }}</CBadge>
              </div>
              <div class="small text-body-secondary">{{ runner.operatingSystem || '—' }}</div>
            </div>
            <CBadge :text-bg-color="statusColors[runner.status]">{{ t(statusLabels[runner.status]) }}</CBadge>
          </div>

          <div class="d-flex justify-content-between small mb-1">
            <span class="text-body-secondary">{{ t('插槽使用率') }}</span>
            <span class="fw-semibold">{{ runner.usedSlots }} / {{ runner.totalSlots }}</span>
          </div>
          <CProgress
            class="progress-thin mb-3"
            :color="statusColors[runner.status]"
            :value="runner.totalSlots === 0 ? 0 : (runner.usedSlots / runner.totalSlots) * 100"
          />

          <div class="d-flex flex-wrap gap-1 mb-3">
            <CBadge v-for="label in runner.labelList" :key="label" color="light" class="border text-body">
              {{ label }}
            </CBadge>
          </div>

          <div class="small text-body-secondary mb-3">
            {{ t('目前工作：{current}｜最後心跳：{lastSeen}', {
              current: runner.currentBuildNumber ? `#${runner.currentBuildNumber} ${runner.currentJobName}` : '—',
              lastSeen: relative(runner.lastSeenAt)
            }) }}
          </div>

          <div class="d-flex flex-wrap gap-2">
            <CButton color="secondary" variant="outline" size="sm" @click="togglePaused(runner)">
              {{ t(runner.isPaused ? '恢復接單' : '暫停接單') }}
            </CButton>
            <CButton color="secondary" variant="outline" size="sm" @click="openEditor(runner)">
              {{ t('編輯') }}
            </CButton>
            <CButton
              v-if="!runner.isLocal"
              color="danger"
              variant="outline"
              size="sm"
              @click="confirmDelete(runner)"
            >
              {{ t('刪除') }}
            </CButton>
          </div>
        </CCardBody>
      </CCard>
    </CCol>
  </CRow>
      </CCardBody>
    </template>
    <template #log>
    <CCardBody class="p-0">
      <div class="table-responsive">
        <CTable align="middle" class="mb-0">
          <CTableHead class="text-nowrap">
            <CTableRow>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('執行器') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('作業系統') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('標籤') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('目前工作') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary" style="width: 12rem">{{ t('插槽使用率') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary text-end">{{ t('狀態') }}</CTableHeaderCell>
            </CTableRow>
          </CTableHead>
          <CTableBody>
            <CTableRow v-for="runner in runnerPage.paged" :key="runner.id">
              <CTableDataCell class="fw-semibold">{{ runner.name }}</CTableDataCell>
              <CTableDataCell class="text-body-secondary">{{ runner.operatingSystem || '—' }}</CTableDataCell>
              <CTableDataCell>
                <CBadge v-for="label in runner.labelList" :key="label" color="light" class="border text-body me-1">
                  {{ label }}
                </CBadge>
              </CTableDataCell>
              <CTableDataCell class="text-body-secondary">
                <template v-if="runner.currentBuildNumber">
                  #{{ runner.currentBuildNumber }} {{ runner.currentJobName }}
                </template>
                <template v-else>—</template>
              </CTableDataCell>
              <CTableDataCell>
                <div class="d-flex align-items-center gap-2">
                  <span class="small fw-semibold text-nowrap" style="width: 3.5rem">{{ runner.usedSlots }} / {{ runner.totalSlots }}</span>
                  <CProgress
                    class="progress-thin flex-grow-1"
                    :color="statusColors[runner.status]"
                    :value="runner.totalSlots === 0 ? 0 : (runner.usedSlots / runner.totalSlots) * 100"
                  />
                </div>
              </CTableDataCell>
              <CTableDataCell class="text-end">
                <CBadge :text-bg-color="statusColors[runner.status]">{{ t(statusLabels[runner.status]) }}</CBadge>
              </CTableDataCell>
            </CTableRow>
          </CTableBody>
        </CTable>
      </div>
    </CCardBody>
    <TablePagination
      :page="runnerPage.page"
      :page-size="runnerPage.pageSize"
      :total="runnerPage.total"
      :pages="runnerPage.pages"
      @update:page="runnerPage.setPage"
      @update:page-size="runnerPage.setPageSize"
    />
    <CCardFooter>
      <div class="small text-body-secondary mb-2">
        {{ t('在目標機器執行下列指令，即可將該機器註冊為 Walis 執行器。') }}
      </div>
      <pre class="bg-body-tertiary border rounded p-3 mb-0"><code>{{ claimCommand }}</code></pre>
      <div class="small text-body-secondary mt-2">
        {{ t('遠端執行器會向 claim API 認領工作，完成後以 result API 回報結果。') }}
      </div>
    </CCardFooter>
    </template>
  </PageTabs>

  <CModal :visible="editorVisible" @close="editorVisible = false">
    <CModalHeader>
      <CModalTitle>{{ t(editing ? '編輯執行器' : '註冊執行器') }}</CModalTitle>
    </CModalHeader>
    <CModalBody>
      <CRow class="g-3">
        <CCol :xs="12">
          <CFormLabel for="runner-name">{{ t('名稱') }}</CFormLabel>
          <CFormInput id="runner-name" v-model="form.name" :invalid="Boolean(errors.name)" />
          <CFormFeedback invalid>{{ errors.name?.[0] }}</CFormFeedback>
        </CCol>
        <CCol :md="6">
          <CFormLabel for="runner-os">{{ t('作業系統') }}</CFormLabel>
          <CFormInput id="runner-os" v-model="form.operatingSystem" placeholder="Ubuntu 24.04" />
        </CCol>
        <CCol :md="6">
          <CFormLabel for="runner-version">{{ t('版本') }}</CFormLabel>
          <CFormInput id="runner-version" v-model="form.version" placeholder="1.0.0" />
        </CCol>
        <CCol :md="6">
          <CFormLabel for="runner-labels">{{ t('標籤') }}</CFormLabel>
          <CFormInput id="runner-labels" v-model="form.labels" placeholder="linux,docker" />
          <CFormText>{{ t('以逗號分隔。') }}</CFormText>
        </CCol>
        <CCol :md="6">
          <CFormLabel for="runner-slots">{{ t('插槽數') }}</CFormLabel>
          <CFormInput
            id="runner-slots"
            v-model.number="form.totalSlots"
            type="number"
            min="1"
            max="32"
            :invalid="Boolean(errors.totalSlots)"
          />
          <CFormFeedback invalid>{{ errors.totalSlots?.[0] }}</CFormFeedback>
        </CCol>
        <CCol :md="6">
          <CFormLabel for="runner-minimum-role">{{ t('接受的任務') }}</CFormLabel>
          <CFormSelect id="runner-minimum-role" v-model="form.minimumRunRole">
            <option value="operator">{{ t('operator 以上的任務') }}</option>
            <option value="admin">{{ t('只接受管理員任務') }}</option>
          </CFormSelect>
          <CFormText>{{ t('執行器不套 Walis 的沙箱；放部署憑證的主機請設為只接受管理員任務。') }}</CFormText>
        </CCol>
        <CCol :xs="12">
          <CFormSwitch id="runner-paused" v-model="form.isPaused" :label="t('暫停接單')" />
        </CCol>
      </CRow>
    </CModalBody>
    <CModalFooter>
      <CButton color="secondary" variant="outline" @click="editorVisible = false">{{ t('取消') }}</CButton>
      <CButton color="primary" @click="save">{{ t('儲存') }}</CButton>
    </CModalFooter>
  </CModal>

  <CModal :visible="pendingDelete !== null" @close="pendingDelete = null">
    <CModalHeader>
      <CModalTitle>{{ t('刪除執行器') }}</CModalTitle>
    </CModalHeader>
    <CModalBody>
      <p class="mb-2">{{ t('確定要刪除執行器「{name}」嗎？', { name: pendingDelete?.name ?? '' }) }}</p>
      <p class="small text-body-secondary mb-0">
        <template v-if="pendingKeyCount !== null">
          {{ t('會一併停用綁定這個執行器的 {count} 把 API 金鑰；同名的執行器之後要重新發金鑰才能認領工作。', { count: pendingKeyCount }) }}
        </template>
        <template v-else>
          {{ t('會一併停用綁定這個執行器的 API 金鑰；同名的執行器之後要重新發金鑰才能認領工作。') }}
        </template>
      </p>
    </CModalBody>
    <CModalFooter>
      <CButton color="secondary" variant="outline" @click="pendingDelete = null">{{ t('取消') }}</CButton>
      <CButton color="danger" @click="remove">{{ t('刪除') }}</CButton>
    </CModalFooter>
  </CModal>
</template>
