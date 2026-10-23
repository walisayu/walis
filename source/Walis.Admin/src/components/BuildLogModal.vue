<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import StatusBadge from './StatusBadge.vue'
import AiAnswerModal from './AiAnswerModal.vue'
import { ApiError, api } from '../services/api'
import { useFormat } from '../composables/useFormat'
import { useAiStore } from '../stores/ai'
import { useToastStore } from '../stores/toasts'
import { parseParameterValues } from '../utils/buildParameters'
import type { AiAnswer, Build, BuildArtifact, BuildStep } from '../types'

const props = defineProps<{ build: Build | null }>()
const emit = defineEmits<{ (event: 'close'): void }>()

/**
 * A running build is refetched every two seconds: the executor writes output back to the database as it runs,
 * so what arrives here is live progress and there is no need to wait for the build to finish.
 */
const live = ref<Build | null>(null)
const logElement = ref<HTMLElement | null>(null)
const followTail = ref(true)
let timer: number | null = null

const current = computed(() => live.value ?? props.build)
const parameters = computed(() => parseParameterValues(current.value?.parametersJson))
const isRunning = computed(() => current.value?.status === 'Running' || current.value?.status === 'Queued')

/** With a single step (a job with a directly edited script) there is no need for an extra level; show the output directly. */
const steps = computed<BuildStep[]>(() => current.value?.steps ?? [])
const showSteps = computed(() => steps.value.length > 1)
const expanded = ref<Set<string>>(new Set())

const stepColors: Record<BuildStep['status'], string> = {
  Pending: 'secondary',
  Running: 'info',
  Succeeded: 'success',
  Failed: 'danger',
  Skipped: 'secondary'
}

const stepLabels: Record<BuildStep['status'], string> = {
  Pending: '等待中',
  Running: '執行中',
  Succeeded: '成功',
  Failed: '失敗',
  Skipped: '略過'
}

function toggleStep(step: BuildStep): void {
  const next = new Set(expanded.value)
  next.has(step.id) ? next.delete(step.id) : next.add(step.id)
  expanded.value = next
}

/** Running steps expand automatically so the viewer does not have to keep clicking. */
watch(steps, (list) => {
  const running = list.find((step) => step.status === 'Running')

  if (running && !expanded.value.has(running.id)) {
    expanded.value = new Set([...expanded.value, running.id])
  }
})

function stopPolling(): void {
  if (timer !== null) {
    window.clearInterval(timer)
    timer = null
  }
}

async function pull(): Promise<void> {
  const id = props.build?.id

  if (!id) {
    return
  }

  try {
    live.value = await api.build(id)
  } catch {
    // If the fetch fails, wait for the next round without bothering the user.
    return
  }

  if (!isRunning.value) {
    stopPolling()
  }

  await scrollToTail()
}

/** When the user scrolls up on their own, do not force the view back to the bottom. */
function onScroll(): void {
  const element = logElement.value

  if (!element) {
    return
  }

  followTail.value = element.scrollHeight - element.scrollTop - element.clientHeight < 40
}

async function scrollToTail(): Promise<void> {
  if (!followTail.value) {
    return
  }

  await nextTick()
  const element = logElement.value

  if (element) {
    element.scrollTop = element.scrollHeight
  }
}

watch(
  () => props.build?.id,
  async (id) => {
    stopPolling()
    live.value = null
    followTail.value = true

    if (!id) {
      return
    }

    await scrollToTail()

    if (isRunning.value) {
      timer = window.setInterval(() => void pull(), 2_000)
    }
  },
  { immediate: true }
)

onBeforeUnmount(stopPolling)

const { t } = useI18n()
const { dateTime, duration } = useFormat()
const toasts = useToastStore()
const ai = useAiStore()

const aiVisible = ref(false)
const aiAnswer = ref<AiAnswer | null>(null)
const aiLoading = ref(false)
const aiError = ref<string | null>(null)

async function analyze(): Promise<void> {
  if (!props.build) {
    return
  }

  aiVisible.value = true
  aiLoading.value = true
  aiError.value = null
  aiAnswer.value = null

  try {
    aiAnswer.value = await api.analyzeBuild(props.build.id)
  } catch (exception) {
    aiError.value = exception instanceof ApiError ? exception.message : String(exception)
  } finally {
    aiLoading.value = false
    await ai.loadRequests().catch(() => undefined)
  }
}

const subtitle = computed(() => {
  const build = current.value

  if (!build) {
    return ''
  }

  return [
    build.runnerName ?? '—',
    dateTime(build.startedAt ?? build.queuedAt),
    duration(build.durationSeconds)
  ].join(' · ')
})

const artifacts = ref<BuildArtifact[]>([])

watch(
  current,
  async (build) => {
    if (!build || build.status === 'Running' || build.status === 'Queued') {
      artifacts.value = []
      return
    }

    artifacts.value = await api.buildArtifacts(build.id).catch(() => [])
  },
  { immediate: true }
)

function artifactUrl(artifact: BuildArtifact): string {
  return api.artifactUrl(artifact.buildId, artifact.id)
}

function formatSize(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} B`
  }

  return bytes < 1024 * 1024 ? `${(bytes / 1024).toFixed(1)} KB` : `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

async function copy(): Promise<void> {
  if (!current.value) {
    return
  }

  try {
    await navigator.clipboard.writeText(current.value.log)
    toasts.success(t('輸出已複製到剪貼簿'))
  } catch {
    toasts.error(t('瀏覽器不允許存取剪貼簿。'))
  }
}
</script>

<template>
  <CModal size="lg" scrollable :visible="build !== null" @close="emit('close')">
    <CModalHeader>
      <div>
        <CModalTitle>
          {{ current ? `#${current.number} ${current.jobName}` : t('建置輸出') }}
          <span v-if="current?.matrixLabel" class="badge text-bg-light border ms-2">{{ current.matrixLabel }}</span>
        </CModalTitle>
        <div class="small text-body-secondary">{{ subtitle }}</div>
        <div v-if="parameters.length" class="d-flex flex-wrap gap-1 mt-1">
          <code v-for="[name, value] in parameters" :key="name" class="small border rounded px-1 bg-body-tertiary">
            {{ name }}={{ value }}
          </code>
        </div>
      </div>
    </CModalHeader>
    <CModalBody>
      <div class="d-flex align-items-center gap-3 mb-3">
        <StatusBadge :status="current?.status" />
        <span class="small text-body-secondary">
          {{ t('觸發') }}：{{ current?.triggeredBy }}
        </span>
        <span class="small text-body-secondary">
          {{ t('結束碼') }}：{{ current?.exitCode ?? '—' }}
        </span>
        <span v-if="isRunning" class="small text-body-secondary d-flex align-items-center gap-1 ms-auto">
          <CSpinner size="sm" />{{ t('執行中，輸出會即時更新') }}
        </span>
      </div>
      <template v-if="showSteps">
        <div class="d-flex flex-column gap-2">
          <div v-for="step in steps" :key="step.id" class="border rounded">
            <button
              type="button"
              class="btn btn-link text-decoration-none w-100 d-flex align-items-center gap-2 px-3 py-2 text-body"
              @click="toggleStep(step)"
            >
              <CIcon :icon="expanded.has(step.id) ? 'cil-chevron-bottom' : 'cil-chevron-right'" size="sm" />
              <span class="text-body-secondary small">{{ step.number }}</span>
              <span class="fw-semibold text-break">{{ step.name }}</span>
              <code v-if="step.stepKey" class="small text-body-secondary">{{ step.stepKey }}</code>
              <span v-if="step.dependsOn?.length" class="small text-body-secondary" :title="t('依賴步驟')">← {{ step.dependsOn.join(', ') }}</span>
              <code v-if="step.condition" class="small text-body-secondary text-truncate" style="max-width: 16rem" :title="step.condition">{{ step.condition }}</code>
              <CSpinner v-if="step.status === 'Running'" size="sm" class="ms-1" />
              <span class="ms-auto d-flex align-items-center gap-2">
                <span v-if="step.continueOnError" class="badge text-bg-light">{{ t('允許失敗') }}</span>
                <span v-if="step.exitCode !== null" class="small text-body-secondary font-monospace">
                  exit {{ step.exitCode }}
                </span>
                <span v-if="step.durationSeconds" class="small text-body-secondary">
                  {{ step.durationSeconds.toFixed(1) }} s
                </span>
                <CBadge :color="stepColors[step.status]">{{ t(stepLabels[step.status]) }}</CBadge>
              </span>
            </button>
            <pre
              v-if="expanded.has(step.id)"
              class="bg-dark text-white rounded-bottom p-3 mb-0 build-log"
            ><code>{{ step.log || t('（這一步還沒有輸出）') }}</code></pre>
            <table
              v-if="expanded.has(step.id) && Object.keys(step.outputs ?? {}).length"
              class="table table-sm table-borderless small font-monospace mb-0 border-top"
            >
              <thead><tr><th class="ps-3">{{ t('輸出') }}</th><th>{{ t('值') }}</th></tr></thead>
              <tbody>
                <tr v-for="(value, name) in step.outputs" :key="name">
                  <td class="ps-3 text-nowrap">{{ name }}</td>
                  <td class="text-break">{{ value }}</td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>
      </template>

      <pre
        v-else
        ref="logElement"
        class="bg-dark text-white rounded p-3 mb-0 build-log"
        @scroll="onScroll"
      ><code>{{ current?.log }}</code></pre>

      <template v-if="artifacts.length">
        <h6 class="mt-4 mb-2">{{ t('產出檔案') }}</h6>
        <CListGroup>
          <a
            v-for="artifact in artifacts"
            :key="artifact.id"
            class="list-group-item list-group-item-action d-flex align-items-center gap-2"
            :href="artifactUrl(artifact)"
            download
          >
            <CIcon icon="cil-cloud-download" class="text-body-secondary" />
            <span class="me-auto font-monospace text-break">{{ artifact.relativePath }}</span>
            <span class="small text-body-secondary">{{ formatSize(artifact.sizeBytes) }}</span>
          </a>
        </CListGroup>
      </template>
    </CModalBody>
    <CModalFooter>
      <CButton color="primary" variant="outline" class="me-auto" @click="analyze">
        <CIcon icon="cil-lightbulb" class="me-1" />{{ t('AI 分析') }}
      </CButton>
      <CButton color="secondary" variant="outline" @click="copy">
        <CIcon icon="cil-copy" class="me-1" />{{ t('複製輸出') }}
      </CButton>
      <CButton color="primary" @click="emit('close')">{{ t('關閉') }}</CButton>
    </CModalFooter>
  </CModal>

  <AiAnswerModal
    :visible="aiVisible"
    :title="t('AI 分析：{name}', { name: build ? `#${build.number} ${build.jobName}` : '' })"
    :answer="aiAnswer"
    :loading="aiLoading"
    :error="aiError"
    @close="aiVisible = false"
    @retry="analyze"
  />
</template>
