<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import TablePagination from '../components/TablePagination.vue'
import PageTabs from '../components/PageTabs.vue'
import { useRoute, useRouter } from 'vue-router'
import PageActions from '../components/PageActions.vue'
import BuildLogModal from '../components/BuildLogModal.vue'
import StatusBadge from '../components/StatusBadge.vue'
import { api } from '../services/api'
import { useFormat } from '../composables/useFormat'
import { useRunnersStore } from '../stores/runners'
import { useToastStore } from '../stores/toasts'
import type { Build, BuildPage, BuildStatus } from '../types'

const { t } = useI18n()

const tab = ref<'builds' | 'stats'>('builds')
const tabs = [
  { key: 'builds', label: '執行紀錄', icon: 'cil-list-rich' },
  { key: 'stats', label: '統計', icon: 'cil-speedometer' }
]
const route = useRoute()
const router = useRouter()
const runners = useRunnersStore()
const toasts = useToastStore()
const { dateTime, duration, relative } = useFormat()

const PAGE_SIZES = [25, 50, 100, 200]
const DEFAULT_PAGE_SIZE = 25

// Builds keep piling up, so filtering and paging are left to the server and only the query conditions live here.
// The conditions sync to the URL, so they survive a reload or navigating back.
const search = ref(String(route.query.search ?? ''))
const statusFilter = ref(String(route.query.status ?? ''))
const runnerFilter = ref(String(route.query.runner ?? ''))
const page = ref(Math.max(1, Number(route.query.page) || 1))
const pageSize = ref(
  PAGE_SIZES.includes(Number(route.query.pageSize)) ? Number(route.query.pageSize) : DEFAULT_PAGE_SIZE
)

const result = ref<BuildPage | null>(null)
const loading = ref(false)
const loadError = ref('')
const selectedBuild = ref<Build | null>(null)
let timer: number | undefined

const items = computed(() => result.value?.items ?? [])
const total = computed(() => result.value?.total ?? 0)
const pages = computed(() => Math.max(1, Math.ceil(total.value / pageSize.value)))
const rangeFrom = computed(() => (total.value === 0 ? 0 : (page.value - 1) * pageSize.value + 1))
const rangeTo = computed(() => Math.min(total.value, page.value * pageSize.value))

// The tally comes from the server and ignores the status filter, so the statistics stay complete while one status is listed.
const counts = computed<Record<BuildStatus, number>>(() => ({
  Queued: 0,
  Running: 0,
  Succeeded: 0,
  Failed: 0,
  Canceled: 0,
  ...(result.value?.counts ?? {})
}))

let requestId = 0

async function load(): Promise<void> {
  const id = ++requestId
  loading.value = true
  loadError.value = ''

  try {
    const response = await api.buildPage({
      search: search.value.trim() || undefined,
      status: statusFilter.value || undefined,
      runner: runnerFilter.value || undefined,
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
  if (runnerFilter.value) query.runner = runnerFilter.value
  if (page.value !== 1) query.page = String(page.value)
  if (pageSize.value !== DEFAULT_PAGE_SIZE) query.pageSize = String(pageSize.value)
  void router.replace({ query })
}

function goTo(target: number): void {
  if (target >= 1 && target <= pages.value && target !== page.value) {
    page.value = target
  }
}

async function refresh(): Promise<void> {
  await load()
}

async function cancel(build: Build): Promise<void> {
  try {
    const result = await api.cancelBuild(build.id)
    await load()
    toasts.push(
      result.status === 'Running'
        ? t('已要求取消建置 #{number}，執行中的步驟會被中止', { number: build.number })
        : t('建置 #{number} 已取消', { number: build.number }),
      'secondary'
    )
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  }
}

function hasActive(): boolean {
  return items.value.some((build) => build.status === 'Queued' || build.status === 'Running')
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

watch([statusFilter, runnerFilter, pageSize], () => {
  page.value = 1
  syncQuery()
  void load()
})

watch(page, () => {
  syncQuery()
  void load()
})

onMounted(async () => {
  await runners.load()

  const id = route.params.id as string | undefined

  if (id) {
    selectedBuild.value = await api.build(id).catch(() => null)
  }

  await load()

  timer = window.setInterval(() => {
    if (hasActive()) {
      void load()
    }
  }, 4_000)
})

onBeforeUnmount(() => {
  window.clearInterval(timer)
  window.clearTimeout(searchTimer)
})
</script>

<template>
    <PageActions>
      <CButton color="secondary" variant="outline" size="sm" @click="refresh">
        <CIcon icon="cil-reload" class="me-1" />{{ t('重新整理') }}
      </CButton>
    </PageActions>

  <PageTabs v-model="tab" :tabs="tabs">
    <template #builds>
    <CCardBody>
      <CRow class="g-3 mb-3">
        <CCol :md="6" :lg="5">
          <CInputGroup>
            <CInputGroupText><CIcon icon="cil-search" /></CInputGroupText>
            <CFormInput
              v-model="search"
              type="search"
              :placeholder="t('搜尋建置編號或任務')"
              :aria-label="t('搜尋建置')"
              @keyup.enter="refresh"
            />
          </CInputGroup>
        </CCol>
        <CCol :xs="6" :md="3">
          <CFormSelect v-model="statusFilter" :aria-label="t('狀態篩選')">
            <option value="">{{ t('所有狀態') }}</option>
            <option value="Succeeded">{{ t('成功') }}</option>
            <option value="Failed">{{ t('失敗') }}</option>
            <option value="Running">{{ t('執行中') }}</option>
            <option value="Queued">{{ t('佇列中') }}</option>
            <option value="Canceled">{{ t('已取消') }}</option>
          </CFormSelect>
        </CCol>
        <CCol :xs="6" :md="3" :lg="4">
          <CFormSelect v-model="runnerFilter" :aria-label="t('執行器篩選')">
            <option value="">{{ t('所有執行器') }}</option>
            <option v-for="runner in runners.items" :key="runner.id" :value="runner.name">{{ runner.name }}</option>
          </CFormSelect>
        </CCol>
      </CRow>

      <div class="table-responsive">
        <CTable hover align="middle" bordered class="mb-0">
          <CTableHead class="text-nowrap">
            <CTableRow>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('編號') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('任務') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('專案') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('觸發') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('執行器') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('開始時間') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('耗時') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary">{{ t('狀態') }}</CTableHeaderCell>
              <CTableHeaderCell class="bg-body-tertiary text-end">{{ t('操作') }}</CTableHeaderCell>
            </CTableRow>
          </CTableHead>
          <CTableBody>
            <CTableRow v-for="build in items" :key="build.id" class="is-clickable" @click="selectedBuild = build">
              <CTableDataCell class="font-monospace text-nowrap">
                #{{ build.number }}
                <span v-if="build.matrixLabel" class="badge text-bg-light border ms-1">{{ build.matrixLabel }}</span>
              </CTableDataCell>
              <CTableDataCell class="fw-semibold">{{ build.jobName }}</CTableDataCell>
              <CTableDataCell class="text-body-secondary">{{ build.jobProject ?? '—' }}</CTableDataCell>
              <CTableDataCell class="text-body-secondary">{{ build.triggeredBy }}</CTableDataCell>
              <CTableDataCell class="text-body-secondary">{{ build.runnerName ?? '—' }}</CTableDataCell>
              <CTableDataCell class="text-body-secondary" :title="dateTime(build.startedAt ?? build.queuedAt)">
                {{ relative(build.startedAt ?? build.queuedAt) }}
              </CTableDataCell>
              <CTableDataCell class="text-body-secondary">{{ duration(build.durationSeconds) }}</CTableDataCell>
              <CTableDataCell><StatusBadge :status="build.status" /></CTableDataCell>
              <CTableDataCell class="text-end text-nowrap" @click.stop>
                <CButton color="primary" variant="ghost" size="sm" :title="t('查看輸出')" @click="selectedBuild = build">
                  <CIcon icon="cil-terminal" />
                </CButton>
                <CButton
                  v-if="build.status === 'Queued' || build.status === 'Running'"
                  color="secondary"
                  variant="ghost"
                  size="sm"
                  :disabled="Boolean(build.cancelRequestedAt)"
                  :title="build.cancelRequestedAt ? t('已要求取消，等待執行器中止') : t('取消')"
                  @click="cancel(build)"
                >
                  <CIcon icon="cil-ban" />
                </CButton>
              </CTableDataCell>
            </CTableRow>

            <CTableRow v-if="items.length === 0">
              <CTableDataCell colspan="9" class="text-center text-body-secondary py-5">
                {{ t('找不到符合條件的建置紀錄') }}
              </CTableDataCell>
            </CTableRow>
          </CTableBody>
        </CTable>
      </div>

      <CAlert v-if="loadError" color="danger" class="mt-3 mb-0">{{ loadError }}</CAlert>
    </CCardBody>

    <TablePagination
      :page="page"
      :page-size="pageSize"
      :total="total"
      :pages="pages"
      :page-sizes="PAGE_SIZES"
      :summary="t('顯示第 {from}–{to} 筆，共 {total} 筆建置', { from: rangeFrom, to: rangeTo, total })"
      @update:page="(value: number) => (page = value)"
      @update:page-size="(value: number) => (pageSize = value)"
    />
    </template>
    <template #stats>
      <CCardBody>
      <CRow class="g-4 mb-4">
        <CCol :sm="6" :xl="3">
          <CCard>
            <CCardBody class="d-flex align-items-center">
              <CAvatar color="success" text-color="white" size="md" class="me-3">
                <CIcon icon="cil-check-circle" size="lg" />
              </CAvatar>
              <div>
                <div class="fs-5 fw-semibold">{{ counts.Succeeded }}</div>
                <div class="small text-body-secondary text-uppercase">{{ t('成功') }}</div>
              </div>
            </CCardBody>
          </CCard>
        </CCol>
        <CCol :sm="6" :xl="3">
          <CCard>
            <CCardBody class="d-flex align-items-center">
              <CAvatar color="danger" text-color="white" size="md" class="me-3">
                <CIcon icon="cil-x-circle" size="lg" />
              </CAvatar>
              <div>
                <div class="fs-5 fw-semibold">{{ counts.Failed }}</div>
                <div class="small text-body-secondary text-uppercase">{{ t('失敗') }}</div>
              </div>
            </CCardBody>
          </CCard>
        </CCol>
        <CCol :sm="6" :xl="3">
          <CCard>
            <CCardBody class="d-flex align-items-center">
              <CAvatar color="warning" text-color="white" size="md" class="me-3">
                <CIcon icon="cil-reload" size="lg" />
              </CAvatar>
              <div>
                <div class="fs-5 fw-semibold">{{ counts.Running + counts.Queued }}</div>
                <div class="small text-body-secondary text-uppercase">{{ t('執行中') }}</div>
              </div>
            </CCardBody>
          </CCard>
        </CCol>
        <CCol :sm="6" :xl="3">
          <CCard>
            <CCardBody class="d-flex align-items-center">
              <CAvatar color="secondary" text-color="white" size="md" class="me-3">
                <CIcon icon="cil-ban" size="lg" />
              </CAvatar>
              <div>
                <div class="fs-5 fw-semibold">{{ counts.Canceled }}</div>
                <div class="small text-body-secondary text-uppercase">{{ t('已取消') }}</div>
              </div>
            </CCardBody>
          </CCard>
        </CCol>
      </CRow>
      </CCardBody>
    </template>
  </PageTabs>

  <BuildLogModal :build="selectedBuild" @close="selectedBuild = null" />
</template>
