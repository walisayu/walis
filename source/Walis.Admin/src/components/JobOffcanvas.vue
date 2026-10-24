<script setup lang="ts">
import { ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import StatusBadge from './StatusBadge.vue'
import { api } from '../services/api'
import { useFormat } from '../composables/useFormat'
import type { Build, Job } from '../types'

const props = defineProps<{ job: Job | null }>()
const emit = defineEmits<{
  (event: 'close'): void
  (event: 'run', job: Job): void
  (event: 'edit', job: Job): void
  (event: 'toggle', job: Job): void
  (event: 'select-build', build: Build): void
}>()

const { t } = useI18n()
const { dateTime, duration, relative } = useFormat()

const history = ref<Build[]>([])
const loading = ref(false)
const tab = ref<'overview' | 'history' | 'command'>('overview')

watch(
  () => props.job,
  async (job) => {
    tab.value = 'overview'
    history.value = []

    if (!job) {
      return
    }

    loading.value = true

    try {
      history.value = await api.jobBuilds(job.id, 8)
    } finally {
      loading.value = false
    }
  }
)

const triggerLabels: Record<string, string> = { manual: '手動', push: '推送後自動', cron: '固定排程' }
</script>

<template>
  <COffcanvas
    placement="end"
    :visible="job !== null"
    style="--cui-offcanvas-width: 34rem"
    @hide="emit('close')"
  >
    <COffcanvasHeader class="border-bottom">
      <COffcanvasTitle>{{ t('任務詳情') }}</COffcanvasTitle>
      <CCloseButton class="text-reset" @click="emit('close')" />
    </COffcanvasHeader>

    <COffcanvasBody v-if="job">
      <div class="d-flex align-items-center mb-3">
        <CAvatar color="primary" text-color="white" size="md" class="fw-semibold rounded">
          {{ job.name.slice(0, 1) }}
        </CAvatar>
        <div class="ms-3 me-auto">
          <h5 class="mb-0">{{ job.name }}</h5>
          <div class="small text-body-secondary">{{ job.project ?? '—' }} · {{ job.branch }}</div>
        </div>
        <CBadge :color="job.isDisabled ? 'secondary' : 'success'" :text-bg-color="job.isDisabled ? 'secondary' : 'success'">
          {{ t(job.isDisabled ? '已暫停' : '啟用中') }}
        </CBadge>
      </div>

      <div class="d-flex flex-wrap gap-2 mb-4">
        <CButton color="primary" size="sm" :disabled="job.isDisabled" @click="emit('run', job)">
          <CIcon icon="cil-media-play" class="me-1" />{{ t('立即執行') }}
        </CButton>
        <CButton color="secondary" variant="outline" size="sm" @click="emit('edit', job)">
          <CIcon icon="cil-pencil" class="me-1" />{{ t('編輯任務') }}
        </CButton>
        <CButton color="secondary" variant="outline" size="sm" @click="emit('toggle', job)">
          {{ t(job.isDisabled ? '啟用' : '暫停') }}
        </CButton>
      </div>

      <CNav variant="underline-border" role="tablist">
        <CNavItem>
          <button class="nav-link" type="button" :class="{ active: tab === 'overview' }" @click="tab = 'overview'">
            {{ t('概覽') }}
          </button>
        </CNavItem>
        <CNavItem>
          <button class="nav-link" type="button" :class="{ active: tab === 'history' }" @click="tab = 'history'">
            {{ t('建置紀錄') }}
          </button>
        </CNavItem>
        <CNavItem>
          <button class="nav-link" type="button" :class="{ active: tab === 'command' }" @click="tab = 'command'">
            {{ t('執行命令') }}
          </button>
        </CNavItem>
      </CNav>

      <div class="pt-3">
        <CListGroup v-if="tab === 'overview'" flush>
          <CListGroupItem class="d-flex justify-content-between gap-3">
            <span class="text-body-secondary">{{ t('觸發方式') }}</span>
            <span class="fw-semibold text-end">
              {{ t(triggerLabels[job.triggerKind]) }}
              <span v-if="job.cron" class="font-monospace">（{{ job.cron }}）</span>
            </span>
          </CListGroupItem>
          <CListGroupItem class="d-flex justify-content-between gap-3">
            <span class="text-body-secondary">{{ t('下次觸發') }}</span>
            <span class="fw-semibold text-end">{{ job.nextRunAt ? dateTime(job.nextRunAt) : '—' }}</span>
          </CListGroupItem>
          <CListGroupItem class="d-flex justify-content-between gap-3">
            <span class="text-body-secondary">{{ t('最近建置') }}</span>
            <span class="fw-semibold text-end">
              <template v-if="job.lastBuildNumber">
                #{{ job.lastBuildNumber }} · {{ relative(job.lastBuildAt) }} · {{ duration(job.lastBuildSeconds) }}
              </template>
              <template v-else>—</template>
            </span>
          </CListGroupItem>
          <CListGroupItem class="d-flex justify-content-between gap-3">
            <span class="text-body-secondary">{{ t('成功率') }}</span>
            <span class="fw-semibold text-end">{{ job.successRate }}%（{{ job.totalBuilds }}）</span>
          </CListGroupItem>
          <CListGroupItem class="d-flex justify-content-between gap-3">
            <span class="text-body-secondary">{{ t('執行器') }}</span>
            <span class="fw-semibold text-end">{{ job.runnerName ?? '—' }}</span>
          </CListGroupItem>
          <CListGroupItem class="d-flex justify-content-between gap-3">
            <span class="text-body-secondary">{{ t('工作目錄') }}</span>
            <span class="fw-semibold text-end font-monospace text-break">{{ job.workingDirectory ?? '—' }}</span>
          </CListGroupItem>
          <CListGroupItem class="d-flex justify-content-between gap-3">
            <span class="text-body-secondary">{{ t('逾時時間') }}</span>
            <span class="fw-semibold text-end">{{ t('{minutes} 分鐘', { minutes: job.timeoutMinutes }) }}</span>
          </CListGroupItem>
          <CListGroupItem class="d-flex justify-content-between gap-3">
            <span class="text-body-secondary">{{ t('產出路徑') }}</span>
            <span class="fw-semibold text-end font-monospace text-break">{{ job.artifactPath ?? '—' }}</span>
          </CListGroupItem>
          <CListGroupItem class="d-flex justify-content-between gap-3">
            <span class="text-body-secondary">{{ t('儲存庫') }}</span>
            <span class="fw-semibold text-end text-break">{{ job.repository ?? '—' }}</span>
          </CListGroupItem>
        </CListGroup>

        <div v-else-if="tab === 'history'">
          <CSpinner v-if="loading" size="sm" />
          <CListGroup v-else-if="history.length" flush>
            <CListGroupItem
              v-for="build in history"
              :key="build.id"
              component="button"
              class="d-flex align-items-center"
              @click="emit('select-build', build)"
            >
              <span class="font-monospace me-3">#{{ build.number }}</span>
              <div class="me-auto text-start">
                <div class="small">{{ build.triggeredBy }} · {{ build.runnerName ?? '—' }}</div>
                <div class="small text-body-secondary">
                  {{ dateTime(build.startedAt ?? build.queuedAt) }} · {{ duration(build.durationSeconds) }}
                </div>
              </div>
              <StatusBadge :status="build.status" />
            </CListGroupItem>
          </CListGroup>
          <p v-else class="text-body-secondary mb-0">{{ t('尚未有建置紀錄。') }}</p>
        </div>

        <pre v-else class="bg-body-tertiary border rounded p-3 mb-0"><code>{{ job.command }}</code></pre>
      </div>
    </COffcanvasBody>
  </COffcanvas>
</template>
