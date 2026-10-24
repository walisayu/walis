<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import PageTabs from '../components/PageTabs.vue'
import { useRouter } from 'vue-router'
import BuildLogModal from '../components/BuildLogModal.vue'
import PageActions from '../components/PageActions.vue'
import SparkChart from '../components/SparkChart.vue'
import StatusBadge from '../components/StatusBadge.vue'
import TrendChart from '../components/TrendChart.vue'
import { useFormat } from '../composables/useFormat'
import { useDashboardStore } from '../stores/dashboard'
import type { Build } from '../types'

const { t } = useI18n()

const tab = ref<'overview' | 'recent' | 'activity' | 'runners' | 'jobs'>('overview')
const tabs = [
  { key: 'overview', label: '總覽', icon: 'cil-speedometer' },
  { key: 'recent', label: '最近建置紀錄', icon: 'cil-list-rich' },
  { key: 'activity', label: '最近活動', icon: 'cil-bell' },
  { key: 'runners', label: '執行器負載', icon: 'cil-terminal' },
  { key: 'jobs', label: '任務成功率', icon: 'cil-task' }
]
const router = useRouter()
const dashboard = useDashboardStore()
const { duration, relative, dateTime } = useFormat()

const selectedBuild = ref<Build | null>(null)
let timer: number | undefined

const summary = computed(() => dashboard.summary)
const trendTotals = computed(() => summary.value?.trend.map((item) => item.total) ?? [])
const trendSucceeded = computed(() => summary.value?.trend.map((item) => item.succeeded) ?? [])
const trendFailed = computed(() => summary.value?.trend.map((item) => item.failed) ?? [])
const runningSeries = computed(() => summary.value?.trend.map((item) => item.total - item.succeeded - item.failed) ?? [])

const runnerUsage = computed(() => {
  const runners = summary.value?.runners ?? []
  const total = runners.reduce((sum, runner) => sum + runner.totalSlots, 0)
  const used = runners.reduce((sum, runner) => sum + runner.usedSlots, 0)
  return total === 0 ? 0 : Math.round((used / total) * 100)
})

const topJobs = computed(() =>
  [...(summary.value?.jobs ?? [])].sort((left, right) => right.totalBuilds - left.totalBuilds).slice(0, 6)
)

async function refresh(): Promise<void> {
  await dashboard.load()
}

onMounted(async () => {
  await refresh()
  timer = window.setInterval(() => {
    if ((summary.value?.runningBuilds ?? 0) > 0 || (summary.value?.queuedBuilds ?? 0) > 0) {
      void refresh()
    }
  }, 5_000)
})

onBeforeUnmount(() => window.clearInterval(timer))
</script>

<template>
  <div v-if="dashboard.loading && !summary" class="d-flex justify-content-center py-5">
    <CSpinner color="primary" />
  </div>

  <template v-else-if="summary">
    <PageActions>
      <CButton color="secondary" variant="outline" size="sm" @click="refresh">
        <CIcon icon="cil-reload" class="me-1" />{{ t('重新整理') }}
      </CButton>
      <CButton v-if="tab === 'recent'" color="secondary" variant="outline" size="sm" @click="router.push('/builds')">
        {{ t('查看全部') }}
      </CButton>
    </PageActions>

    <PageTabs v-model="tab" :tabs="tabs">
      <template #overview>
        <CCardBody>
    <CRow class="g-4 mb-4">
      <CCol :sm="6" :xl="3">
        <CWidgetStatsA color="primary" :value="String(summary.enabledJobs)" :title="t('啟用中的任務')">
          <template #action>
            <CDropdown placement="bottom-end">
              <CDropdownToggle color="transparent" class="text-white p-0" :caret="false">
                <CIcon icon="cil-options" />
              </CDropdownToggle>
              <CDropdownMenu>
                <CDropdownItem component="button" @click="router.push('/jobs')">{{ t('查看所有任務') }}</CDropdownItem>
                <CDropdownItem component="button" @click="router.push('/jobs/new')">{{ t('新增任務') }}</CDropdownItem>
              </CDropdownMenu>
            </CDropdown>
          </template>
          <template #chart>
            <SparkChart :values="trendTotals" />
          </template>
        </CWidgetStatsA>
      </CCol>

      <CCol :sm="6" :xl="3">
        <CWidgetStatsA color="info" :value="`${summary.successRateLast7Days}%`" :title="t('近 7 日成功率')">
          <template #action>
            <CDropdown placement="bottom-end">
              <CDropdownToggle color="transparent" class="text-white p-0" :caret="false">
                <CIcon icon="cil-options" />
              </CDropdownToggle>
              <CDropdownMenu>
                <CDropdownItem component="button" @click="router.push('/builds')">{{ t('查看執行紀錄') }}</CDropdownItem>
              </CDropdownMenu>
            </CDropdown>
          </template>
          <template #chart>
            <SparkChart :values="trendSucceeded" />
          </template>
        </CWidgetStatsA>
      </CCol>

      <CCol :sm="6" :xl="3">
        <CWidgetStatsA color="warning" :value="String(summary.runningBuilds)" :title="t('目前執行中')">
          <template #action>
            <CDropdown placement="bottom-end">
              <CDropdownToggle color="transparent" class="text-white p-0" :caret="false">
                <CIcon icon="cil-options" />
              </CDropdownToggle>
              <CDropdownMenu>
                <CDropdownItem component="button" @click="router.push('/runners')">{{ t('查看執行器') }}</CDropdownItem>
                <CDropdownItem component="button" @click="router.push('/builds')">{{ t('查看佇列') }}</CDropdownItem>
              </CDropdownMenu>
            </CDropdown>
          </template>
          <template #chart>
            <SparkChart :values="runningSeries" fill />
          </template>
        </CWidgetStatsA>
      </CCol>

      <CCol :sm="6" :xl="3">
        <CWidgetStatsA color="danger" :value="String(summary.failuresLast7Days)" :title="t('近 7 日失敗次數')">
          <template #action>
            <CDropdown placement="bottom-end">
              <CDropdownToggle color="transparent" class="text-white p-0" :caret="false">
                <CIcon icon="cil-options" />
              </CDropdownToggle>
              <CDropdownMenu>
                <CDropdownItem component="button" @click="router.push('/builds')">{{ t('查看失敗紀錄') }}</CDropdownItem>
                <CDropdownItem component="button" @click="router.push('/notifications')">{{ t('通知設定') }}</CDropdownItem>
              </CDropdownMenu>
            </CDropdown>
          </template>
          <template #chart>
            <SparkChart :values="trendFailed" kind="bar" />
          </template>
        </CWidgetStatsA>
      </CCol>
    </CRow>
        </CCardBody>
      <CCardBody>
        <h4 class="card-title mb-0">{{ t('建置趨勢') }}</h4>
        <div class="small text-body-secondary">{{ t('最近 7 天') }}</div>
        <TrendChart :trend="summary.trend" />
      </CCardBody>
      <CCardFooter>
        <CRow class="row-cols-1 row-cols-sm-2 row-cols-lg-4 g-4 mb-2 text-center">
          <CCol>
            <div class="text-body-secondary">{{ t('建置次數') }}</div>
            <div class="fw-semibold text-truncate">{{ summary.buildsLast7Days }}</div>
            <CProgress class="progress-thin mt-2" color="success" :value="100" />
          </CCol>
          <CCol>
            <div class="text-body-secondary">{{ t('平均耗時') }}</div>
            <div class="fw-semibold text-truncate">{{ duration(summary.averageDurationSeconds) }}</div>
            <CProgress class="progress-thin mt-2" color="info" :value="Math.min(100, summary.averageDurationSeconds)" />
          </CCol>
          <CCol>
            <div class="text-body-secondary">{{ t('佇列等待') }}</div>
            <div class="fw-semibold text-truncate">{{ duration(summary.averageQueueSeconds) }}</div>
            <CProgress class="progress-thin mt-2" color="warning" :value="Math.min(100, summary.averageQueueSeconds)" />
          </CCol>
          <CCol>
            <div class="text-body-secondary">{{ t('執行器使用率') }}</div>
            <div class="fw-semibold text-truncate">{{ runnerUsage }}%</div>
            <CProgress class="progress-thin mt-2" :value="runnerUsage" />
          </CCol>
        </CRow>
      </CCardFooter>
      </template>
      <template #recent>
        <CCardBody class="p-0">
          <div class="table-responsive">
            <CTable hover align="middle" class="mb-0">
              <CTableHead class="text-nowrap">
                <CTableRow>
                  <CTableHeaderCell class="bg-body-tertiary">{{ t('編號') }}</CTableHeaderCell>
                  <CTableHeaderCell class="bg-body-tertiary">{{ t('任務') }}</CTableHeaderCell>
                  <CTableHeaderCell class="bg-body-tertiary">{{ t('觸發') }}</CTableHeaderCell>
                  <CTableHeaderCell class="bg-body-tertiary">{{ t('開始時間') }}</CTableHeaderCell>
                  <CTableHeaderCell class="bg-body-tertiary">{{ t('執行器') }}</CTableHeaderCell>
                  <CTableHeaderCell class="bg-body-tertiary">{{ t('耗時') }}</CTableHeaderCell>
                  <CTableHeaderCell class="bg-body-tertiary text-end">{{ t('狀態') }}</CTableHeaderCell>
                </CTableRow>
              </CTableHead>
              <CTableBody>
                <CTableRow
                  v-for="build in summary.recentBuilds"
                  :key="build.id"
                  class="is-clickable"
                  @click="selectedBuild = build"
                >
                  <CTableDataCell class="font-monospace">#{{ build.number }}</CTableDataCell>
                  <CTableDataCell class="fw-semibold">{{ build.jobName }}</CTableDataCell>
                  <CTableDataCell class="text-body-secondary">{{ build.triggeredBy }}</CTableDataCell>
                  <CTableDataCell class="text-body-secondary text-nowrap">{{ relative(build.startedAt ?? build.queuedAt) }}</CTableDataCell>
                  <CTableDataCell class="text-body-secondary">{{ build.runnerName ?? '—' }}</CTableDataCell>
                  <CTableDataCell class="text-body-secondary">{{ duration(build.durationSeconds) }}</CTableDataCell>
                  <CTableDataCell class="text-end"><StatusBadge :status="build.status" /></CTableDataCell>
                </CTableRow>
                <CTableRow v-if="summary.recentBuilds.length === 0">
                  <CTableDataCell colspan="7" class="text-center text-body-secondary py-5">
                    {{ t('找不到符合條件的建置紀錄') }}
                  </CTableDataCell>
                </CTableRow>
              </CTableBody>
            </CTable>
          </div>
        </CCardBody>
      </template>
      <template #activity>
        <CListGroup flush>
          <CListGroupItem v-for="(item, index) in summary.activity" :key="index" class="d-flex align-items-start">
            <StatusBadge :status="item.kind" class="me-3 mt-1" />
            <div class="me-auto">
              <div class="fw-semibold">{{ item.jobName }}</div>
              <div class="small text-body-secondary">
                #{{ item.buildNumber }} · {{ item.runnerName ?? '—' }}
              </div>
            </div>
            <span class="small text-body-secondary text-nowrap ms-2" :title="dateTime(item.occurredAt)">
              {{ relative(item.occurredAt) }}
            </span>
          </CListGroupItem>
          <CListGroupItem v-if="summary.activity.length === 0" class="text-body-secondary">
            {{ t('尚未有建置紀錄。') }}
          </CListGroupItem>
        </CListGroup>
      </template>
      <template #runners>
        <CCardBody>
          <div v-for="runner in summary.runners" :key="runner.id" class="progress-group mb-4">
            <div class="progress-group-header d-flex align-items-center mb-2">
              <CIcon icon="cil-terminal" size="lg" class="me-2 text-body-secondary" />
              <span>{{ runner.name }}</span>
              <span class="ms-auto fw-semibold">
                {{ runner.usedSlots }} / {{ runner.totalSlots }}
                <span class="text-body-secondary small ms-2">{{ t(runner.status === 'Idle' ? '待命' : runner.status === 'Busy' ? '執行中' : runner.status === 'Paused' ? '已暫停' : '離線') }}</span>
              </span>
            </div>
            <div class="progress-group-bars">
              <CProgress
                class="progress-thin"
                :color="runner.status === 'Offline' ? 'secondary' : runner.status === 'Busy' ? 'warning' : 'success'"
                :value="runner.totalSlots === 0 ? 0 : (runner.usedSlots / runner.totalSlots) * 100"
              />
            </div>
          </div>
        </CCardBody>
      </template>
      <template #jobs>
        <CCardBody>
          <div v-for="job in topJobs" :key="job.id" class="progress-group mb-4">
            <div class="progress-group-header d-flex align-items-center mb-2">
              <span>{{ job.name }}</span>
              <span class="ms-auto fw-semibold">{{ job.successRate }}%</span>
            </div>
            <div class="progress-group-bars">
              <CProgress
                class="progress-thin"
                :color="job.successRate >= 98 ? 'success' : job.successRate >= 90 ? 'info' : 'danger'"
                :value="job.successRate"
              />
            </div>
          </div>
          <p v-if="topJobs.length === 0" class="text-body-secondary mb-0">{{ t('找不到符合條件的任務') }}</p>
        </CCardBody>
      </template>
    </PageTabs>


    <BuildLogModal :build="selectedBuild" @close="selectedBuild = null" />
  </template>
</template>
