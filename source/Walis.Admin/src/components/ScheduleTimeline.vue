<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ApiError, api } from '../services/api'
import { useFormat } from '../composables/useFormat'
import type { ScheduleTimeline, ScheduleTimelineBuild, ScheduleTimelineJob } from '../types'

// Columns are time ticks, rows are jobs: the past shows builds that actually ran (click to see the log), the future shows the trigger times estimated from Cron,
// with lengths from the recent average duration. The window starts at 00:00 of a day in the system time zone and is decided by the server; this only draws.
const emit = defineEmits<{ (event: 'select-build', buildId: string): void }>()

const { t } = useI18n()
const { dateTime, time, shortDate, duration } = useFormat()

const days = ref<1 | 3 | 7 | 14 | 30>(1)
const date = ref<string | null>(null)
const scaleMinutes = ref(60)
const search = ref('')
const project = ref('')
const onlyScheduled = ref(false)
const loading = ref(false)
const error = ref('')
const data = ref<ScheduleTimeline | null>(null)

const scales = [
  { value: 15, label: '15 分鐘', width: 48 },
  { value: 30, label: '30 分鐘', width: 56 },
  { value: 60, label: '1 小時', width: 64 },
  { value: 180, label: '3 小時', width: 88 },
  { value: 1440, label: '1 天', width: 200 },
  { value: 10080, label: '1 週', width: 280 }
]

const ranges: { value: 1 | 3 | 7 | 14 | 30; label: string }[] = [
  { value: 1, label: '1 天' },
  { value: 3, label: '3 天' },
  { value: 7, label: '7 天' },
  { value: 14, label: '14 天' },
  { value: 30, label: '30 天' }
]

const NAME_WIDTH = 220
const ROW_HEIGHT = 36

const tickWidth = computed(() => scales.find((scale) => scale.value === scaleMinutes.value)?.width ?? 64)
const totalMinutes = computed(() => (data.value?.days ?? days.value) * 1440)
const tickCount = computed(() => Math.ceil(totalMinutes.value / scaleMinutes.value))
const totalWidth = computed(() => tickCount.value * tickWidth.value)
const pxPerMinute = computed(() => tickWidth.value / scaleMinutes.value)
const fromMs = computed(() => (data.value ? Date.parse(data.value.from) : 0))
const toMs = computed(() => (data.value ? Date.parse(data.value.to) : 0))
// With ticks shorter than a day, the first cell of each day shows the date and the rest show the time; with ticks of a day or more every cell shows the date.
const ticksPerDay = computed(() => Math.max(1, 1440 / scaleMinutes.value))

const ticks = computed(() =>
  Array.from({ length: tickCount.value }, (_, index) => {
    const at = new Date(fromMs.value + index * scaleMinutes.value * 60_000).toISOString()
    const isDay = scaleMinutes.value >= 1440 || index % ticksPerDay.value === 0
    return { index, at, label: isDay ? shortDate(at) : time(at), isDay }
  })
)

// When the range is stretched to two weeks or more, fine ticks would draw thousands of cells; switch to a coarser tick automatically.
watch(days, (value) => {
  if (value >= 14 && scaleMinutes.value < 180) {
    scaleMinutes.value = value >= 30 ? 1440 : 180
  }
})

const projects = computed(() =>
  [...new Set((data.value?.jobs ?? []).map((job) => job.project).filter((value): value is string => Boolean(value)))].sort()
)

const jobs = computed(() => {
  const keyword = search.value.trim().toLowerCase()
  return (data.value?.jobs ?? []).filter((job) => {
    if (onlyScheduled.value && job.triggerKind !== 'cron') return false
    if (project.value && job.project !== project.value) return false
    if (!keyword) return true
    return `${job.name} ${job.project ?? ''} ${job.cron ?? ''}`.toLowerCase().includes(keyword)
  })
})

const now = ref(Date.now())
const nowLeft = computed(() => (now.value >= fromMs.value && now.value < toMs.value ? offset(now.value) : null))

function offset(ms: number): number {
  return ((ms - fromMs.value) / 60_000) * pxPerMinute.value
}

function width(seconds: number | null | undefined, fallback = 60): number {
  return Math.max(6, ((seconds ?? fallback) / 60) * pxPerMinute.value)
}

interface Block {
  key: string
  left: number
  width: number
  kind: 'build' | 'planned' | 'missed' | 'paused'
  status?: string
  title: string
  buildId?: string
}

function estimate(job: ScheduleTimelineJob): number | null {
  return job.averageSeconds ?? job.lastBuildSeconds ?? null
}

function blocks(job: ScheduleTimelineJob): Block[] {
  const list: Block[] = []
  const estimated = estimate(job)

  for (const build of job.builds) {
    const start = Date.parse(build.startedAt ?? build.queuedAt)
    list.push({
      key: `b-${build.id}`,
      left: offset(start),
      width: width(build.durationSeconds ?? estimated),
      kind: 'build',
      status: build.status,
      buildId: build.id,
      title: `#${build.number} · ${dateTime(build.startedAt ?? build.queuedAt)} · ${duration(build.durationSeconds)} · ${statusLabel(build)}`
    })
  }

  // Estimated trigger times: those already covered by a build are not redrawn; past ones that never ran are drawn faded.
  for (const start of job.starts) {
    const ms = Date.parse(start)
    const covered = job.builds.some((build) => Math.abs(Date.parse(build.startedAt ?? build.queuedAt) - ms) < 90_000)
    if (covered) continue

    const past = ms < now.value
    list.push({
      key: `p-${start}`,
      left: offset(ms),
      width: width(estimated),
      kind: job.isDisabled ? 'paused' : past ? 'missed' : 'planned',
      title: job.isDisabled
        ? `${dateTime(start)} · ${t('已暫停，不會觸發')}`
        : past
          ? `${dateTime(start)} · ${t('沒有建置紀錄')}`
          : `${dateTime(start)} · ${estimated ? t('預估 {duration}', { duration: duration(estimated) }) : t('沒有歷史耗時')}`
    })
  }

  return list
}

const triggerLabels: Record<string, string> = { manual: '手動', push: '推送後自動', cron: '固定排程' }

const statusLabels: Record<string, string> = {
  Succeeded: '成功',
  Failed: '失敗',
  Running: '執行中',
  Queued: '佇列中',
  Canceled: '已取消'
}

function statusLabel(build: ScheduleTimelineBuild): string {
  return t(statusLabels[build.status] ?? build.status)
}

async function load(): Promise<void> {
  loading.value = true
  error.value = ''
  try {
    data.value = await api.scheduleTimeline(date.value, days.value)
    date.value = data.value.date
    now.value = Date.now()
  } catch (exception) {
    error.value = exception instanceof ApiError ? exception.message : String(exception)
  } finally {
    loading.value = false
  }
}

function shift(direction: -1 | 1): void {
  if (!date.value) return
  const [year, month, dayOfMonth] = date.value.split('-').map(Number)
  const moved = new Date(Date.UTC(year!, month! - 1, dayOfMonth! + direction * days.value))
  date.value = moved.toISOString().slice(0, 10)
}

function today(): void {
  date.value = null
  void load()
}

watch([date, days], () => void load())

let timer: number | undefined
onMounted(() => {
  void load()
  timer = window.setInterval(() => (now.value = Date.now()), 30_000)
})

onBeforeUnmount(() => window.clearInterval(timer))

defineExpose({ load })
</script>

<template>
  <div>
    <div class="d-flex flex-wrap align-items-center gap-2 mb-3">
      <CButtonGroup size="sm" role="group" :aria-label="t('範圍')">
        <CButton
          v-for="range in ranges"
          :key="range.value"
          :color="days === range.value ? 'primary' : 'secondary'"
          :variant="days === range.value ? undefined : 'outline'"
          @click="days = range.value"
        >
          {{ t(range.label) }}
        </CButton>
      </CButtonGroup>
      <CButtonGroup size="sm" role="group">
        <CButton color="secondary" variant="outline" :title="t('上一段')" @click="shift(-1)">‹</CButton>
        <CButton color="secondary" variant="outline" @click="today">{{ t('今天') }}</CButton>
        <CButton color="secondary" variant="outline" :title="t('下一段')" @click="shift(1)">›</CButton>
      </CButtonGroup>
      <span v-if="data" class="small text-body-secondary">{{ shortDate(data.from) }} – {{ shortDate(new Date(toMs - 1).toISOString()) }}</span>
      <CFormSelect v-model.number="scaleMinutes" size="sm" style="width: auto" :aria-label="t('刻度')">
        <option v-for="scale in scales" :key="scale.value" :value="scale.value">{{ t('刻度') }}：{{ t(scale.label) }}</option>
      </CFormSelect>
      <CFormInput v-model="search" size="sm" type="search" style="width: 14rem" :placeholder="t('搜尋任務')" />
      <CFormSelect v-model="project" size="sm" style="width: auto" :aria-label="t('專案')">
        <option value="">{{ t('所有專案') }}</option>
        <option v-for="name in projects" :key="name" :value="name">{{ name }}</option>
      </CFormSelect>
      <CFormSwitch id="tl-only-scheduled" v-model="onlyScheduled" class="mb-0" :label="t('只看排程任務')" />
      <CSpinner v-if="loading" size="sm" class="ms-auto" />
    </div>

    <CAlert v-if="error" color="danger" class="py-2">{{ error }}</CAlert>

    <div v-else-if="data" class="tl border rounded">
      <div class="tl-names" :style="{ width: `${NAME_WIDTH}px` }">
        <div class="tl-head tl-corner"></div>
        <div
          v-for="job in jobs"
          :key="job.id"
          class="tl-name text-truncate"
          :class="{ 'text-body-secondary': job.isDisabled }"
          :style="{ height: `${ROW_HEIGHT}px` }"
          :title="`${job.name}\n${job.cron ?? ''}${job.cronError ? `\n${job.cronError}` : ''}`"
        >
          <span class="fw-semibold">{{ job.name }}</span>
          <code class="ms-2 small text-body-secondary">{{ job.cron }}</code>
          <CBadge v-if="job.triggerKind !== 'cron'" color="light" class="border text-body ms-2">{{ t(triggerLabels[job.triggerKind] ?? job.triggerKind) }}</CBadge>
          <CBadge v-if="job.isDisabled" color="secondary" class="ms-2">{{ t('已暫停') }}</CBadge>
          <CBadge v-if="job.cronError" color="danger" class="ms-2" :title="job.cronError">{{ t('Cron 無法解析') }}</CBadge>
        </div>
        <div v-if="jobs.length === 0" class="tl-name text-body-secondary" :style="{ height: `${ROW_HEIGHT}px` }">
          {{ t(data.jobs.length === 0 ? '沒有排程任務' : '沒有符合條件的排程') }}
        </div>
      </div>

      <div class="tl-scroll">
        <div :style="{ width: `${totalWidth}px` }">
          <div class="tl-head d-flex">
            <div
              v-for="tick in ticks"
              :key="tick.index"
              class="tl-tick small text-body-secondary"
              :class="{ 'tl-tick-day': tick.isDay }"
              :style="{ width: `${tickWidth}px` }"
            >
              {{ tick.label }}
            </div>
          </div>

          <div class="tl-body position-relative">
            <div
              v-for="job in jobs"
              :key="job.id"
              class="tl-row position-relative"
              :style="{ height: `${ROW_HEIGHT}px`, backgroundSize: `${tickWidth}px 100%` }"
            >
              <button
                v-for="block in blocks(job)"
                :key="block.key"
                type="button"
                class="tl-block"
                :class="[`tl-${block.kind}`, block.status ? `tl-status-${block.status.toLowerCase()}` : '']"
                :style="{ left: `${block.left}px`, width: `${block.width}px` }"
                :title="block.title"
                :disabled="!block.buildId"
                @click="block.buildId && emit('select-build', block.buildId)"
              ></button>
            </div>
            <div v-if="jobs.length === 0" :style="{ height: `${ROW_HEIGHT}px` }"></div>
            <div v-if="nowLeft !== null" class="tl-now" :style="{ left: `${nowLeft}px` }" :title="t('現在')"></div>
          </div>
        </div>
      </div>
    </div>

    <div class="d-flex flex-wrap gap-3 small text-body-secondary mt-2">
      <span><span class="tl-legend tl-status-succeeded"></span>{{ t('成功') }}</span>
      <span><span class="tl-legend tl-status-failed"></span>{{ t('失敗') }}</span>
      <span><span class="tl-legend tl-status-running"></span>{{ t('執行中') }}</span>
      <span><span class="tl-legend tl-planned"></span>{{ t('預估') }}</span>
      <span><span class="tl-legend tl-missed"></span>{{ t('沒有建置紀錄') }}</span>
      <span><span class="tl-legend tl-paused"></span>{{ t('已暫停') }}</span>
    </div>
  </div>
</template>

<style scoped>
.tl {
  display: flex;
  overflow: hidden;
  background: var(--cui-card-bg, var(--cui-body-bg));
}

.tl-names {
  flex: 0 0 auto;
  border-right: 1px solid var(--cui-border-color);
  background: var(--cui-body-bg);
}

.tl-head {
  height: 32px;
  border-bottom: 1px solid var(--cui-border-color);
  background: var(--cui-tertiary-bg);
}

.tl-name {
  display: flex;
  align-items: center;
  padding: 0 .75rem;
  border-bottom: 1px solid var(--cui-border-color-translucent);
}

.tl-scroll {
  flex: 1 1 auto;
  overflow-x: auto;
}

.tl-tick {
  flex: 0 0 auto;
  padding: .35rem .25rem 0;
  border-left: 1px solid var(--cui-border-color-translucent);
  white-space: nowrap;
  overflow: hidden;
}

.tl-tick-day {
  border-left-color: var(--cui-border-color);
  font-weight: 600;
  color: var(--cui-body-color);
}

.tl-row {
  border-bottom: 1px solid var(--cui-border-color-translucent);
  background-image: linear-gradient(to right, var(--cui-border-color-translucent) 1px, transparent 1px);
}

.tl-block {
  position: absolute;
  top: 8px;
  height: 20px;
  padding: 0;
  border: 1px solid transparent;
  border-radius: 3px;
  background: var(--cui-primary);
  cursor: pointer;
}

.tl-block:disabled {
  cursor: default;
}

.tl-planned {
  background: rgba(var(--cui-primary-rgb), .25);
  border-color: var(--cui-primary);
  border-style: dashed;
}

.tl-missed {
  background: transparent;
  border-color: var(--cui-border-color);
  border-style: dashed;
}

.tl-paused {
  background: var(--cui-tertiary-bg);
  border-color: var(--cui-border-color);
  opacity: .7;
}

.tl-status-succeeded { background: var(--cui-success); }
.tl-status-failed { background: var(--cui-danger); }
.tl-status-running { background: var(--cui-warning); }
.tl-status-queued { background: var(--cui-secondary); }
.tl-status-canceled { background: var(--cui-secondary); opacity: .6; }

.tl-block.tl-build:hover {
  filter: brightness(1.1);
  box-shadow: 0 0 0 2px rgba(var(--cui-primary-rgb), .3);
}

.tl-now {
  position: absolute;
  top: 0;
  bottom: 0;
  width: 2px;
  background: var(--cui-danger);
  pointer-events: none;
}

.tl-legend {
  display: inline-block;
  width: 14px;
  height: 10px;
  margin-right: .35rem;
  border: 1px solid transparent;
  border-radius: 2px;
  vertical-align: middle;
}

.tl-legend.tl-planned,
.tl-legend.tl-missed {
  border-style: dashed;
}

.tl-legend.tl-planned { border-color: var(--cui-primary); background: rgba(var(--cui-primary-rgb), .25); }
.tl-legend.tl-missed { border-color: var(--cui-border-color); background: transparent; }
</style>
