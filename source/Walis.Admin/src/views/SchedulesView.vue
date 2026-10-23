<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import TablePagination from '../components/TablePagination.vue'
import { usePagedRows } from '../composables/usePagedRows'
import PageTabs from '../components/PageTabs.vue'
import { useRouter } from 'vue-router'
import { useFormat } from '../composables/useFormat'
import { useJobsStore } from '../stores/jobs'
import { useSettingsStore } from '../stores/settings'
import { useToastStore } from '../stores/toasts'
import PageActions from '../components/PageActions.vue'
import BuildLogModal from '../components/BuildLogModal.vue'
import BuildParametersModal from '../components/BuildParametersModal.vue'
import ScheduleTimeline from '../components/ScheduleTimeline.vue'
import { api } from '../services/api'
import { parseParameterDefinitions } from '../utils/buildParameters'
import type { Build, Job } from '../types'

const { t } = useI18n()

    const tab = ref<'timeline' | 'list' | 'upcoming' | 'cheatsheet'>('timeline')
    const tabs = [
      { key: 'timeline', label: '時間表', icon: 'cil-clock' },
      { key: 'list', label: '排程清單', icon: 'cil-list-rich' },
      { key: 'upcoming', label: '未來 24 小時', icon: 'cil-bell' },
      { key: 'cheatsheet', label: 'Cron 速查', icon: 'cil-info' }
    ]
    const router = useRouter()
const jobs = useJobsStore()
const settings = useSettingsStore()
const toasts = useToastStore()
const { dateTime, relative } = useFormat()

const cheatSheet = [
  { label: '每 15 分鐘', value: '*/15 * * * *', color: 'info' },
  { label: '每日凌晨 2:30', value: '30 2 * * *', color: 'success' },
  { label: '每週五 18:00', value: '0 18 * * 5', color: 'warning' },
  { label: '每月 1 號 00:00', value: '0 0 1 * *', color: 'danger' }
]

const upcoming = computed(() =>
  jobs.scheduled
    .filter((job) => job.nextRunAt)
    .sort((left, right) => (left.nextRunAt ?? '').localeCompare(right.nextRunAt ?? ''))
    .slice(0, 8)
)

async function toggle(job: Job, enabled: boolean): Promise<void> {
  try {
    await jobs.setEnabled(job.id, enabled)
    toasts.push(
      t(enabled ? '「{name}」排程已啟用' : '「{name}」排程已停用', { name: job.name }),
      enabled ? 'success' : 'secondary'
    )
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  }
}

const parameterPrompt = ref<Job | null>(null)
const selectedBuild = ref<Build | null>(null)

/** Clicking a past build on the timeline: fetch the full build (with log) and open the dialog. */
async function openBuild(id: string): Promise<void> {
  try {
    selectedBuild.value = await api.build(id)
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  }
}

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
    await jobs.run(job.id, parameters)
    toasts.info(t('已開始執行「{name}」', { name: job.name }))
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  }
}

onMounted(() => Promise.all([jobs.load(true), settings.load()]))

const schedulePage = usePagedRows(() => jobs.scheduled)
</script>

<template>
  <PageActions>
    <CButton color="primary" size="sm" @click="router.push('/jobs/new')">
      <CIcon icon="cil-plus" class="me-1" />{{ t('新增排程') }}
    </CButton>
  </PageActions>

      <PageTabs v-model="tab" :tabs="tabs">
        <template #timeline>
        <CCardBody>
      <ScheduleTimeline @select-build="openBuild" />
    </CCardBody>
    </template>
        <template #list>
            <CCardBody class="p-0">
          <div class="table-responsive">
            <CTable align="middle" class="mb-0">
              <CTableHead class="text-nowrap">
                <CTableRow>
                  <CTableHeaderCell class="bg-body-tertiary">{{ t('任務') }}</CTableHeaderCell>
                  <CTableHeaderCell class="bg-body-tertiary">{{ t('專案') }}</CTableHeaderCell>
                  <CTableHeaderCell class="bg-body-tertiary">{{ t('Cron 運算式') }}</CTableHeaderCell>
                  <CTableHeaderCell class="bg-body-tertiary">{{ t('下次觸發') }}</CTableHeaderCell>
                  <CTableHeaderCell class="bg-body-tertiary">{{ t('上次觸發') }}</CTableHeaderCell>
                  <CTableHeaderCell class="bg-body-tertiary">{{ t('啟用') }}</CTableHeaderCell>
                  <CTableHeaderCell class="bg-body-tertiary text-end">{{ t('操作') }}</CTableHeaderCell>
                </CTableRow>
              </CTableHead>
              <CTableBody>
                <CTableRow v-for="job in schedulePage.paged" :key="job.id">
                  <CTableDataCell class="fw-semibold">{{ job.name }}</CTableDataCell>
                  <CTableDataCell class="text-body-secondary">{{ job.project ?? '—' }}</CTableDataCell>
                  <CTableDataCell class="font-monospace">{{ job.cron }}</CTableDataCell>
                  <CTableDataCell class="text-body-secondary">
                    {{ job.nextRunAt ? dateTime(job.nextRunAt) : '—' }}
                  </CTableDataCell>
                  <CTableDataCell class="text-body-secondary">{{ relative(job.lastBuildAt) }}</CTableDataCell>
                  <CTableDataCell>
                    <CFormSwitch
                      :model-value="!job.isDisabled"
                      :aria-label="t('啟用排程')"
                      @update:model-value="(value: boolean) => toggle(job, value)"
                    />
                  </CTableDataCell>
                  <CTableDataCell class="text-end text-nowrap">
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
                  </CTableDataCell>
                </CTableRow>

                <CTableRow v-if="jobs.scheduled.length === 0">
                  <CTableDataCell colspan="7" class="text-center text-body-secondary py-5">
                    {{ t('尚未建立排程') }}
                  </CTableDataCell>
                </CTableRow>
              </CTableBody>
            </CTable>
          </div>
        </CCardBody>
        <TablePagination
          :page="schedulePage.page"
          :page-size="schedulePage.pageSize"
          :total="schedulePage.total"
          :pages="schedulePage.pages"
          @update:page="schedulePage.setPage"
          @update:page-size="schedulePage.setPageSize"
        />
    </template>
        <template #upcoming>
            <CListGroup flush>
          <CListGroupItem v-for="job in upcoming" :key="job.id" class="d-flex align-items-center">
            <CAvatar color="secondary" size="sm" class="me-3"><CIcon icon="cil-clock" /></CAvatar>
            <div class="me-auto">
              <div class="fw-semibold">{{ job.name }}</div>
              <div class="small text-body-secondary font-monospace">{{ job.cron }}</div>
            </div>
            <span class="text-nowrap">{{ dateTime(job.nextRunAt) }}</span>
          </CListGroupItem>
          <CListGroupItem v-if="upcoming.length === 0" class="text-body-secondary">
            {{ t('尚未建立排程') }}
          </CListGroupItem>
        </CListGroup>
        <CCardFooter class="text-body-secondary small">
          {{ t('時區：{zone}', { zone: settings.settings?.timeZone ?? 'UTC' }) }}
        </CCardFooter>
    </template>
        <template #cheatsheet>
        <CCardBody>
      <CRow class="g-3">
        <CCol v-for="item in cheatSheet" :key="item.value" :md="6" :xl="3">
          <div class="border-start border-start-4 px-3" :class="`border-start-${item.color}`">
            <div class="small text-body-secondary text-truncate">{{ t(item.label) }}</div>
            <div class="fw-semibold font-monospace">{{ item.value }}</div>
          </div>
        </CCol>
      </CRow>
    </CCardBody>
    </template>
      </PageTabs>

      <BuildLogModal :build="selectedBuild" @close="selectedBuild = null" />
      <BuildParametersModal :job="parameterPrompt" @close="parameterPrompt = null" @run="runWith" />
</template>
