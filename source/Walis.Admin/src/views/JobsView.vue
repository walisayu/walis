<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import TablePagination from '../components/TablePagination.vue'
import { useRoute, useRouter } from 'vue-router'
import PageActions from '../components/PageActions.vue'
import BuildLogModal from '../components/BuildLogModal.vue'
import BuildParametersModal from '../components/BuildParametersModal.vue'
import JenkinsImportModal from '../components/JenkinsImportModal.vue'
import JobOffcanvas from '../components/JobOffcanvas.vue'
import StatusBadge from '../components/StatusBadge.vue'
import { api } from '../services/api'
import { useFormat } from '../composables/useFormat'
import { useJobsStore } from '../stores/jobs'
import { useToastStore } from '../stores/toasts'
import { parseParameterDefinitions } from '../utils/buildParameters'
import type { Build, Job, JobPage, JobSortKey, TriggerKind } from '../types'

const { t } = useI18n()
const router = useRouter()
const route = useRoute()
const jobs = useJobsStore()
const toasts = useToastStore()
const { relative, duration, dateTime } = useFormat()

const PAGE_SIZES = [25, 50, 100, 200]
const DEFAULT_PAGE_SIZE = 25
const SORT_KEYS: JobSortKey[] = ['name', 'project', 'lastBuild', 'updated']

// There may be thousands of jobs: filtering, sorting and paging are left to the server, and this only keeps the query conditions and the current page.
// The conditions sync to the URL, so they survive a reload or navigating back.
const search = ref(String(route.query.search ?? ''))
const statusFilter = ref<'' | 'enabled' | 'paused'>(pick(route.query.status, ['enabled', 'paused']) ?? '')
const triggerFilter = ref<'' | TriggerKind>(pick(route.query.trigger, ['manual', 'push', 'cron']) ?? '')
const projectFilter = ref(String(route.query.project ?? ''))
const sort = ref<JobSortKey>(pick(route.query.sort, SORT_KEYS) ?? 'name')
const order = ref<'asc' | 'desc'>(pick(route.query.order, ['asc', 'desc']) ?? 'asc')
const page = ref(Math.max(1, Number(route.query.page) || 1))
const pageSize = ref(PAGE_SIZES.includes(Number(route.query.pageSize)) ? Number(route.query.pageSize) : DEFAULT_PAGE_SIZE)

const result = ref<JobPage | null>(null)
const loading = ref(false)
const loadError = ref('')
const selectedJob = ref<Job | null>(null)
const selectedBuild = ref<Build | null>(null)
const pendingDelete = ref<Job | null>(null)

const triggerLabels: Record<string, string> = { manual: '手動', push: '推送後自動', cron: '固定排程' }

function pick<T extends string>(value: unknown, allowed: readonly T[]): T | null {
  return typeof value === 'string' && (allowed as readonly string[]).includes(value) ? (value as T) : null
}

const items = computed(() => result.value?.items ?? [])
const total = computed(() => result.value?.total ?? 0)
const pages = computed(() => Math.max(1, Math.ceil(total.value / pageSize.value)))
const rangeFrom = computed(() => (total.value === 0 ? 0 : (page.value - 1) * pageSize.value + 1))
const rangeTo = computed(() => Math.min(total.value, page.value * pageSize.value))
const hasFilters = computed(
  () => search.value.trim() !== '' || statusFilter.value !== '' || triggerFilter.value !== '' || projectFilter.value !== ''
)

let requestId = 0

async function load(): Promise<void> {
  const id = ++requestId
  loading.value = true
  loadError.value = ''

  try {
    const response = await api.jobPage({
      search: search.value.trim() || undefined,
      status: statusFilter.value || undefined,
      trigger: triggerFilter.value || undefined,
      project: projectFilter.value || undefined,
      sort: sort.value,
      order: order.value,
      page: page.value,
      pageSize: pageSize.value
    })

    // A slow old request arriving late must not overwrite a newer one.
    if (id !== requestId) {
      return
    }

    result.value = response

    // When filtering reduces the page count and the current page is out of range: fall back to the last page and fetch again.
    const last = Math.max(1, Math.ceil(response.total / response.pageSize))
    if (page.value > last) {
      page.value = last
    }
  } catch (error) {
    if (id === requestId) {
      loadError.value = error instanceof Error ? error.message : String(error)
    }
  } finally {
    if (id === requestId) {
      loading.value = false
    }
  }
}

function syncQuery(): void {
  const query: Record<string, string> = {}
  if (search.value.trim()) query.search = search.value.trim()
  if (statusFilter.value) query.status = statusFilter.value
  if (triggerFilter.value) query.trigger = triggerFilter.value
  if (projectFilter.value) query.project = projectFilter.value
  if (sort.value !== 'name') query.sort = sort.value
  if (order.value !== 'asc') query.order = order.value
  if (page.value !== 1) query.page = String(page.value)
  if (pageSize.value !== DEFAULT_PAGE_SIZE) query.pageSize = String(pageSize.value)
  void router.replace({ query })
}

// Searching waits 300 ms after typing before querying; any other condition queries immediately; changing a condition always returns to the first page.
let searchTimer: number | undefined
watch(search, () => {
  window.clearTimeout(searchTimer)
  searchTimer = window.setTimeout(() => {
    page.value = 1
    syncQuery()
    void load()
  }, 300)
})

watch([statusFilter, triggerFilter, projectFilter, pageSize], () => {
  page.value = 1
  syncQuery()
  void load()
})

watch([page, sort, order], () => {
  syncQuery()
  void load()
})

function searchNow(): void {
  window.clearTimeout(searchTimer)
  page.value = 1
  syncQuery()
  void load()
}

function sortBy(key: JobSortKey): void {
  if (sort.value === key) {
    order.value = order.value === 'asc' ? 'desc' : 'asc'
  } else {
    sort.value = key
    order.value = key === 'lastBuild' || key === 'updated' ? 'desc' : 'asc'
  }
  page.value = 1
}

function clearFilters(): void {
  window.clearTimeout(searchTimer)
  search.value = ''
  statusFilter.value = ''
  triggerFilter.value = ''
  projectFilter.value = ''
  page.value = 1
  syncQuery()
  void load()
}

function goTo(target: number): void {
  page.value = Math.min(Math.max(1, target), pages.value)
}

onBeforeUnmount(() => window.clearTimeout(searchTimer))

const parameterPrompt = ref<Job | null>(null)
const jenkinsVisible = ref(false)

/** Jobs with declared parameters open the form to fill values first; the rest run directly. */
function run(job: Job): void {
  if (parseParameterDefinitions(job).length > 0) {
    parameterPrompt.value = job
    return
  }

  void runWith(job)
}

async function runWith(job: Job, parameters?: Record<string, string>): Promise<void> {
  parameterPrompt.value = null

  try {
    await api.runJob(job.id, 'manual', parameters)
    toasts.info(t('已開始執行「{name}」', { name: job.name }))
    void load()
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  }
}

async function toggle(job: Job): Promise<void> {
  try {
    const updated = await jobs.setEnabled(job.id, job.isDisabled)
    selectedJob.value = null
    void load()
    toasts.push(
      t(updated.isDisabled ? '「{name}」已暫停' : '「{name}」已啟用', { name: job.name }),
      updated.isDisabled ? 'secondary' : 'success'
    )
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  }
}

async function remove(): Promise<void> {
  const job = pendingDelete.value

  if (!job) {
    return
  }

  try {
    await jobs.remove(job.id)
    toasts.error(t('已刪除「{name}」', { name: job.name }))
    void load()
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  } finally {
    pendingDelete.value = null
  }
}

onMounted(() => {
  void load()
  // The header quick search and the environment variables page still use the full list; this only makes sure it has been loaded.
  void jobs.load()
})

const importText = ref<string | null>(null)
const importOverwrite = ref(false)
const importing = ref(false)

/** Exports as a file download; falls back to copying to the clipboard when the browser sandbox does not allow saving. */
async function exportJobs(): Promise<void> {
  try {
    const bundle = await api.exportJobs()
    const text = JSON.stringify(bundle, null, 2)
    const url = URL.createObjectURL(new Blob([text], { type: 'application/json' }))
    const link = document.createElement('a')

    link.href = url
    link.download = `walis-jobs-${new Date().toISOString().slice(0, 10)}.json`
    link.click()
    URL.revokeObjectURL(url)
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  }
}

async function runImport(): Promise<void> {
  if (!importText.value) {
    return
  }

  importing.value = true

  try {
    const bundle = JSON.parse(importText.value)
    const result = await api.importJobs(bundle, importOverwrite.value)

    toasts.success(
      t('已匯入：新增 {created}、更新 {updated}、略過 {skipped}', {
        created: result.created,
        updated: result.updated,
        skipped: result.skipped
      })
    )

    importText.value = null
    await Promise.all([load(), jobs.load(true)])
  } catch (error) {
    toasts.error(error instanceof SyntaxError ? t('內容不是有效的 JSON。') : String(error))
  } finally {
    importing.value = false
  }
}
</script>

<template>
  <CCard class="mb-4">
    <CCardHeader class="d-flex flex-wrap align-items-center gap-2">
      <span class="me-auto">
        {{ t('建置任務') }}
        <span v-if="result" class="text-body-secondary small ms-2">
          {{ t('{total} 個任務 · {enabled} 個啟用中', { total: result.totalAll, enabled: result.enabledAll }) }}
        </span>
      </span>
    </CCardHeader>
    <PageActions>
      <CButton color="secondary" variant="outline" size="sm" :disabled="loading" @click="load()">
        <CIcon icon="cil-reload" class="me-1" />{{ t('重新整理') }}
      </CButton>
      <CButton color="secondary" variant="outline" size="sm" @click="exportJobs">
        <CIcon icon="cil-cloud-download" class="me-1" />{{ t('匯出') }}
      </CButton>
      <CButton color="secondary" variant="outline" size="sm" @click="importText = ''">
        <CIcon icon="cil-cloud-upload" class="me-1" />{{ t('匯入') }}
      </CButton>
      <CButton color="secondary" variant="outline" size="sm" @click="jenkinsVisible = true">
        <CIcon icon="cil-loop-circular" class="me-1" />{{ t('從 Jenkins 匯入') }}
      </CButton>
      <CButton color="primary" size="sm" @click="router.push('/jobs/new')">
        <CIcon icon="cil-plus" class="me-1" />{{ t('新增任務') }}
      </CButton>
    </PageActions>

    <CCardBody>
      <CRow class="g-2 mb-3 align-items-center">
        <CCol :md="6" :xl="4">
          <CInputGroup>
            <CInputGroupText><CIcon icon="cil-search" /></CInputGroupText>
            <CFormInput
              v-model="search"
              type="search"
              :placeholder="t('搜尋任務名稱、專案或分支')"
              :aria-label="t('搜尋任務')"
              @keydown.enter.prevent="searchNow"
            />
          </CInputGroup>
        </CCol>
        <CCol :xs="6" :md="3" :xl="2">
          <CFormSelect v-model="statusFilter" :aria-label="t('狀態篩選')">
            <option value="">{{ t('所有狀態') }}</option>
            <option value="enabled">{{ t('啟用中') }}</option>
            <option value="paused">{{ t('已暫停') }}</option>
          </CFormSelect>
        </CCol>
        <CCol :xs="6" :md="3" :xl="2">
          <CFormSelect v-model="triggerFilter" :aria-label="t('觸發方式篩選')">
            <option value="">{{ t('所有觸發方式') }}</option>
            <option value="manual">{{ t('手動') }}</option>
            <option value="push">{{ t('推送後自動') }}</option>
            <option value="cron">{{ t('固定排程') }}</option>
          </CFormSelect>
        </CCol>
        <CCol :xs="6" :md="3" :xl="2">
          <CFormSelect v-model="projectFilter" :aria-label="t('專案')">
            <option value="">{{ t('所有專案') }}</option>
            <option v-for="project in result?.projects ?? []" :key="project" :value="project">{{ project }}</option>
            <option v-if="projectFilter && !(result?.projects ?? []).includes(projectFilter)" :value="projectFilter">
              {{ projectFilter }}
            </option>
          </CFormSelect>
        </CCol>
        <CCol :xs="6" :md="3" :xl="2" class="d-flex gap-2">
          <CFormSelect v-model.number="pageSize" :aria-label="t('每頁 {size} 筆', { size: pageSize })">
            <option v-for="size in PAGE_SIZES" :key="size" :value="size">{{ t('每頁 {size} 筆', { size }) }}</option>
          </CFormSelect>
          <CButton
            v-if="hasFilters"
            color="secondary"
            variant="outline"
            class="text-nowrap"
            :title="t('清除篩選')"
            @click="clearFilters"
          >
            <CIcon icon="cil-x" />
          </CButton>
        </CCol>
      </CRow>

      <CAlert v-if="loadError" color="danger" class="d-flex align-items-center">
        <span class="me-auto">{{ loadError }}</span>
        <CButton color="danger" variant="outline" size="sm" @click="load()">{{ t('重新整理') }}</CButton>
      </CAlert>

      <div class="table-responsive position-relative" :class="{ 'opacity-50': loading && result }">
        <CTable hover align="middle" bordered class="mb-0">
          <CTableHead class="text-nowrap">
            <CTableRow>
              <CTableHeaderCell
                class="bg-body-tertiary is-clickable user-select-none"
                :aria-sort="sort === 'name' ? (order === 'asc' ? 'ascending' : 'descending') : 'none'"
                :title="t('依此欄排序')"
                @click="sortBy('name')"
              >
                {{ t('任務') }}
                <CIcon v-if="sort === 'name'" :icon="order === 'asc' ? 'cil-arrow-top' : 'cil-arrow-bottom'" size="sm" class="ms-1" />
              </CTableHeaderCell>
              <CTableHeaderCell
                class="bg-body-tertiary is-clickable user-select-none"
                :aria-sort="sort === 'project' ? (order === 'asc' ? 'ascending' : 'descending') : 'none'"
                :title="t('依此欄排序')"
                @click="sortBy('project')"
              >
                {{ t('專案') }}
                <CIcon v-if="sort === 'project'" :icon="order === 'asc' ? 'cil-arrow-top' : 'cil-arrow-bottom'" size="sm" class="ms-1" />
              </CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('分支') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('狀態') }}</CTableHeaderCell>
              <CTableHeaderCell
                class="bg-body-tertiary is-clickable user-select-none"
                :aria-sort="sort === 'lastBuild' ? (order === 'asc' ? 'ascending' : 'descending') : 'none'"
                :title="t('依此欄排序')"
                @click="sortBy('lastBuild')"
              >
                {{ t('上次建置') }}
                <CIcon v-if="sort === 'lastBuild'" :icon="order === 'asc' ? 'cil-arrow-top' : 'cil-arrow-bottom'" size="sm" class="ms-1" />
              </CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('觸發方式') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary text-end">{{ t('操作') }}</CTableHeaderCell>
            </CTableRow>
          </CTableHead>
          <CTableBody>
            <CTableRow v-for="job in items" :key="job.id" class="is-clickable" @click="selectedJob = job">
              <CTableDataCell class="fw-semibold">{{ job.name }}</CTableDataCell>
              <CTableDataCell class="text-body-secondary">{{ job.project ?? '—' }}</CTableDataCell>
              <CTableDataCell class="text-body-secondary font-monospace">{{ job.branch }}</CTableDataCell>
              <CTableDataCell>
                <CBadge :text-bg-color="job.isDisabled ? 'secondary' : 'success'">
                  {{ t(job.isDisabled ? '已暫停' : '啟用中') }}
                </CBadge>
              </CTableDataCell>
              <CTableDataCell class="text-nowrap">
                <StatusBadge :status="job.lastBuildStatus" />
                <span v-if="job.lastBuildNumber" class="text-body-secondary ms-2" :title="dateTime(job.lastBuildAt)">
                  #{{ job.lastBuildNumber }} · {{ relative(job.lastBuildAt) }} · {{ duration(job.lastBuildSeconds) }}
                </span>
              </CTableDataCell>
              <CTableDataCell class="text-body-secondary text-nowrap">
                {{ t(triggerLabels[job.triggerKind]) }}
                <code v-if="job.cron" class="ms-1">{{ job.cron }}</code>
              </CTableDataCell>
              <CTableDataCell class="text-end text-nowrap" @click.stop>
                <CButton
                  color="success"
                  variant="ghost"
                  size="sm"
                  :title="t('立即執行')"
                  :disabled="job.isDisabled"
                  @click="run(job)"
                >
                  <CIcon icon="cil-media-play" />
                </CButton>
                <CButton
                  color="primary"
                  variant="ghost"
                  size="sm"
                  :title="t('編輯')"
                  @click="router.push(`/jobs/${job.id}/edit`)"
                >
                  <CIcon icon="cil-pencil" />
                </CButton>
                <CButton
                  color="secondary"
                  variant="ghost"
                  size="sm"
                  :title="t('複製任務')"
                  @click="router.push({ path: '/jobs/new', query: { copy: job.id } })"
                >
                  <CIcon icon="cil-copy" />
                </CButton>
                <CDropdown placement="bottom-end" class="d-inline-block" teleport>
                  <CDropdownToggle color="secondary" variant="ghost" size="sm" :caret="false" :title="t('更多操作')">
                    <CIcon icon="cil-options" />
                  </CDropdownToggle>
                  <CDropdownMenu>
                    <CDropdownItem component="button" @click="selectedJob = job">{{ t('查看詳情') }}</CDropdownItem>
                    <CDropdownItem component="button" @click="toggle(job)">
                      {{ t(job.isDisabled ? '啟用任務' : '暫停任務') }}
                    </CDropdownItem>
                    <CDropdownDivider />
                    <CDropdownItem component="button" class="text-danger" @click="pendingDelete = job">
                      {{ t('刪除任務') }}
                    </CDropdownItem>
                  </CDropdownMenu>
                </CDropdown>
              </CTableDataCell>
            </CTableRow>

            <CTableRow v-if="items.length === 0">
              <CTableDataCell colspan="7" class="text-center text-body-secondary py-5">
                <template v-if="loading">
                  <CSpinner size="sm" class="me-2" />{{ t('載入中…') }}
                </template>
                <template v-else>{{ t('找不到符合條件的任務') }}</template>
              </CTableDataCell>
            </CTableRow>
          </CTableBody>
        </CTable>
      </div>
    </CCardBody>

    <TablePagination
      :page="page"
      :page-size="pageSize"
      :total="total"
      :pages="pages"
      :page-sizes="PAGE_SIZES"
      :summary="t('顯示第 {from}–{to} 筆，共 {total} 個任務', { from: rangeFrom, to: rangeTo, total })"
      @update:page="(value: number) => (page = value)"
      @update:page-size="(value: number) => (pageSize = value)"
    />
  </CCard>

  <JobOffcanvas
    :job="selectedJob"
    @close="selectedJob = null"
    @run="run"
    @edit="(job) => router.push(`/jobs/${job.id}/edit`)"
    @toggle="toggle"
    @select-build="(build) => (selectedBuild = build)"
  />

  <BuildLogModal :build="selectedBuild" @close="selectedBuild = null" />

  <BuildParametersModal :job="parameterPrompt" @close="parameterPrompt = null" @run="runWith" />

  <JenkinsImportModal :visible="jenkinsVisible" @close="jenkinsVisible = false" />

  <CModal :visible="pendingDelete !== null" @close="pendingDelete = null">
    <CModalHeader>
      <CModalTitle>{{ t('刪除任務') }}</CModalTitle>
    </CModalHeader>
    <CModalBody>
      {{ t('確定要刪除「{name}」嗎？相關的建置紀錄也會一併刪除。', { name: pendingDelete?.name ?? '' }) }}
    </CModalBody>
    <CModalFooter>
      <CButton color="secondary" variant="outline" @click="pendingDelete = null">{{ t('取消') }}</CButton>
      <CButton color="danger" @click="remove">{{ t('刪除') }}</CButton>
    </CModalFooter>
  </CModal>
  <CModal size="lg" :visible="importText !== null" @close="importText = null">
    <CModalHeader>
      <CModalTitle>{{ t('匯入任務') }}</CModalTitle>
    </CModalHeader>
    <CModalBody>
      <p class="text-body-secondary small">
        {{ t('貼上匯出的 JSON。任務以名稱比對：同名的預設略過，勾選覆寫才會更新內容（識別碼與建置紀錄保留）。') }}
      </p>
      <CFormTextarea v-model="importText" class="font-monospace" rows="12" />
      <CFormSwitch id="import-overwrite" v-model="importOverwrite" class="mt-3" :label="t('覆寫同名任務')" />
    </CModalBody>
    <CModalFooter>
      <CButton color="secondary" variant="outline" @click="importText = null">{{ t('取消') }}</CButton>
      <CButton color="primary" :disabled="importing || !importText" @click="runImport">
        <CSpinner v-if="importing" size="sm" class="me-2" />{{ t('匯入') }}
      </CButton>
    </CModalFooter>
  </CModal>
</template>
