<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import draggable from 'vuedraggable'
import ActionFlowGraph from './ActionFlowGraph.vue'
import ActionLibraryModal from './ActionLibraryModal.vue'
import ActionParameters from './ActionParameters.vue'
import { ancestorsOf, dependenciesOf, makeExplicit } from '../../utils/stepGraph'
import { useActionsStore } from '../../stores/actions'
import { useToastStore } from '../../stores/toasts'
import type { ActionDefinition, ActionStep, StepReferenceSource } from '../../types'

const props = defineProps<{ modelValue: ActionStep[] }>()
const emit = defineEmits<{ (event: 'update:modelValue', value: ActionStep[]): void }>()

const { t } = useI18n()
const actions = useActionsStore()
const toasts = useToastStore()

const search = ref('')
const category = ref('all')
const selectedId = ref<string | null>(null)
const libraryVisible = ref(false)
const previewVisible = ref(false)
const previewScript = ref('')
const previewErrors = ref<string[]>([])

const categoryLabels: Record<string, string> = {
  general: '通用',
  file: '檔案',
  git: 'Git',
  node: 'Node',
  dotnet: '.NET',
  container: '容器',
  deploy: '部署',
  check: '檢查',
  python: 'Python',
  java: 'Java',
  go: 'Go',
  rust: 'Rust',
  ruby: 'Ruby',
  php: 'PHP',
  database: '資料庫',
  cloud: '雲端',
  kubernetes: 'Kubernetes',
  test: '測試',
  security: '安全',
  notify: '通知',
  archive: '封存'
}

const steps = computed({
  get: () => props.modelValue,
  set: (value: ActionStep[]) => {
    emit('update:modelValue', value)

    if (selectedId.value === null || !value.some((step) => step.id === selectedId.value)) {
      selectedId.value = value.at(-1)?.id ?? null
    }
  }
})

const palette = computed(() => {
  const keyword = search.value.trim().toLowerCase()

  return actions.definitions.filter((definition) => {
    const matchesCategory = category.value === 'all' || definition.category === category.value
    const haystack = `${definition.name}${definition.description}${definition.id}`.toLowerCase()
    return matchesCategory && haystack.includes(keyword)
  })
})

const selected = computed(() => steps.value.find((step) => step.id === selectedId.value) ?? null)
const selectedDefinition = computed(() =>
  selected.value ? actions.byId.get(selected.value.actionId) ?? null : null
)

/** The keyed predecessors of the selected step (upstream in the dependency chain, transitively), offered for inserting references into parameters. */
const references = computed<StepReferenceSource[]>(() => {
  if (!selected.value) {
    return []
  }
  const upstream = ancestorsOf(steps.value, selected.value.id)
  return steps.value
    .filter((step) => upstream.has(step.id) && step.enabled && step.key)
    .map((step) => ({ key: step.key!, title: titleOf(step), outputs: definitionOf(step)?.outputs ?? [] }))
})

// List/graph views; remembered in the browser.
const view = ref<'list' | 'graph'>(readView())

function readView(): 'list' | 'graph' {
  try {
    return localStorage.getItem('walis.flow.view') === 'graph' ? 'graph' : 'list'
  } catch {
    return 'list'
  }
}

function setView(value: 'list' | 'graph'): void {
  view.value = value
  try {
    localStorage.setItem('walis.flow.view', value)
  } catch {
    // If it cannot be saved, never mind
  }
}

/** The list view shows each step's dependencies, but only when they differ from "depends on the previous step", so not every row has text. */
const dependencyLabels = computed(() => {
  const dependencies = dependenciesOf(steps.value)
  const labels = new Map<string, string>()
  steps.value.forEach((step, index) => {
    const deps = dependencies.get(step.id) ?? []
    const previous = index > 0 ? [steps.value[index - 1].id] : []
    const isDefault = deps.length === previous.length && deps.every((id) => previous.includes(id))
    if (!isDefault) {
      labels.set(
        step.id,
        deps.length ? deps.map((id) => titleOf(steps.value.find((item) => item.id === id)!)).join('、') : t('起點')
      )
    }
  })
  return labels
})

function connect(from: string, to: string): void {
  const explicit = makeExplicit(steps.value)
  steps.value = explicit.map((step) =>
    step.id === to && !(step.dependsOn ?? []).includes(from) ? { ...step, dependsOn: [...(step.dependsOn ?? []), from] } : step
  )
}

function disconnect(from: string, to: string): void {
  const explicit = makeExplicit(steps.value)
  steps.value = explicit.map((step) =>
    step.id === to ? { ...step, dependsOn: (step.dependsOn ?? []).filter((id) => id !== from) } : step
  )
}

watch(
  () => props.modelValue,
  (value) => {
    if (selectedId.value && !value.some((step) => step.id === selectedId.value)) {
      selectedId.value = value.at(-1)?.id ?? null
    }
  }
)

function definitionOf(step: ActionStep): ActionDefinition | undefined {
  return actions.byId.get(step.actionId)
}

function titleOf(step: ActionStep): string {
  return step.name?.trim() || definitionOf(step)?.name || step.actionId
}

function add(definition: ActionDefinition): void {
  const step = actions.createStep(definition)
  // In an explicit graph a new step is attached after the last step by default, matching the intuition of the list.
  if (steps.value.some((item) => item.dependsOn !== undefined && item.dependsOn !== null)) {
    const last = steps.value.at(-1)
    step.dependsOn = last ? [last.id] : []
  }
  steps.value = [...steps.value, step]
  selectedId.value = step.id
}

function duplicate(step: ActionStep): void {
  const copy: ActionStep = { ...step, id: crypto.randomUUID(), parameters: { ...step.parameters } }
  const index = steps.value.findIndex((item) => item.id === step.id)
  const next = [...steps.value]

  next.splice(index + 1, 0, copy)
  steps.value = next
  selectedId.value = copy.id
}

function remove(step: ActionStep): void {
  // When a step is removed, the steps that depended on it depend on its upstream instead, so the graph does not break.
  const dependencies = dependenciesOf(steps.value)
  const upstream = dependencies.get(step.id) ?? []
  const explicit = steps.value.some((item) => item.dependsOn !== undefined && item.dependsOn !== null)
  steps.value = steps.value
    .filter((item) => item.id !== step.id)
    .map((item) => {
      if (!explicit || !(item.dependsOn ?? []).includes(step.id)) {
        return item
      }
      const rest = (item.dependsOn ?? []).filter((id) => id !== step.id)
      return { ...item, dependsOn: [...rest, ...upstream.filter((id) => !rest.includes(id))] }
    })
}

function move(step: ActionStep, offset: number): void {
  const index = steps.value.findIndex((item) => item.id === step.id)
  const target = index + offset

  if (target < 0 || target >= steps.value.length) {
    return
  }

  const next = [...steps.value]
  const [moved] = next.splice(index, 1)

  next.splice(target, 0, moved)
  steps.value = next
}

function patch(step: ActionStep, changes: Partial<ActionStep>): void {
  // The first dependency edit turns the whole flow into an explicit graph; other steps keep the dependencies derived so far.
  const base = 'dependsOn' in changes ? makeExplicit(steps.value) : steps.value
  steps.value = base.map((item) => (item.id === step.id ? { ...item, ...changes } : item))
}

async function preview(): Promise<void> {
  try {
    const result = await actions.preview(steps.value)
    previewScript.value = result.script
    previewErrors.value = result.errors
    previewVisible.value = true
  } catch (error) {
    toasts.error(error instanceof Error ? error.message : String(error))
  }
}

// The left and right columns can collapse into a narrow strip to give the flow in the middle room; the state is remembered in the browser for next time.
const paletteCollapsed = ref(readCollapsed('walis.flow.palette'))
const settingsCollapsed = ref(readCollapsed('walis.flow.settings'))

function readCollapsed(key: string): boolean {
  try {
    return localStorage.getItem(key) === '1'
  } catch {
    return false
  }
}

function togglePalette(): void {
  paletteCollapsed.value = !paletteCollapsed.value
  try {
    localStorage.setItem('walis.flow.palette', paletteCollapsed.value ? '1' : '0')
  } catch {
    // Cannot be saved in private windows and the like; ignore.
  }
}

function toggleSettings(): void {
  settingsCollapsed.value = !settingsCollapsed.value
  try {
    localStorage.setItem('walis.flow.settings', settingsCollapsed.value ? '1' : '0')
  } catch {
    // Same as above.
  }
}

/** When a step is clicked while the right side is collapsed, expand it, otherwise the click seems to do nothing. */
function select(id: string): void {
  selectedId.value = id
  if (settingsCollapsed.value) {
    toggleSettings()
  }
}

onMounted(async () => {
  await actions.load()

  if (selectedId.value === null) {
    selectedId.value = steps.value[0]?.id ?? null
  }
})
</script>

<template>
  <div class="flow-layout">
    <div class="flow-side" :class="{ 'is-collapsed': paletteCollapsed }">
      <button v-if="paletteCollapsed" type="button" class="flow-strip" :title="t('展開動作面板')" @click="togglePalette">
        <CIcon icon="cil-layers" />
        <span class="flow-strip-label">{{ t('動作') }}</span>
        <span aria-hidden="true">›</span>
      </button>
      <CCard v-else class="h-100">
        <CCardHeader class="d-flex align-items-center gap-2">
          <span class="me-auto">{{ t('動作') }}</span>
          <CButton color="secondary" variant="ghost" size="sm" :title="t('管理動作')" @click="libraryVisible = true">
            <CIcon icon="cil-settings" />
          </CButton>
          <CButton color="secondary" variant="ghost" size="sm" :title="t('收合動作面板')" @click="togglePalette">‹</CButton>
        </CCardHeader>
        <CCardBody class="p-2">
          <CFormInput v-model="search" size="sm" type="search" class="mb-2" :placeholder="t('搜尋動作')" />
          <CFormSelect v-model="category" size="sm" class="mb-2">
            <option value="all">{{ t('所有分類') }}</option>
            <option v-for="item in actions.categories" :key="item" :value="item">
              {{ t(categoryLabels[item] ?? item) }}
            </option>
          </CFormSelect>

          <draggable
            :list="palette"
            :group="{ name: 'actions', pull: 'clone', put: false }"
            :clone="actions.createStep"
            :sort="false"
            item-key="id"
            class="action-palette"
          >
            <template #item="{ element }">
              <button type="button" class="action-card w-100 text-start" @click="add(element)">
                <CIcon :icon="element.icon" class="me-2 text-body-secondary" />
                <span class="fw-semibold">{{ element.name }}</span>
                <CBadge v-if="element.source" color="light" class="border text-body ms-1" :title="element.source">{{ t('套件') }}</CBadge>
                <CBadge v-else-if="!element.isBuiltIn" color="light" class="border text-body ms-1">{{ t('自訂') }}</CBadge>
                <span class="d-block small text-body-secondary">{{ element.description }}</span>
              </button>
            </template>
          </draggable>

          <p v-if="palette.length === 0" class="small text-body-secondary mb-0 px-1">
            {{ t('找不到符合條件的動作') }}
          </p>
        </CCardBody>
      </CCard>
    </div>

    <div class="flow-main">
      <CCard class="h-100">
        <CCardHeader class="d-flex align-items-center gap-2">
          <span class="me-auto">
            {{ t('流程') }}
            <span class="text-body-secondary small ms-2">{{ t('{count} 個步驟', { count: steps.length }) }}</span>
          </span>
          <CButtonGroup size="sm" role="group">
            <CButton color="secondary" :variant="view === 'list' ? undefined : 'outline'" @click="setView('list')">
              <CIcon icon="cil-list" class="me-1" />{{ t('清單') }}
            </CButton>
            <CButton color="secondary" :variant="view === 'graph' ? undefined : 'outline'" @click="setView('graph')">
              <CIcon icon="cil-sitemap" class="me-1" />{{ t('圖') }}
            </CButton>
          </CButtonGroup>
          <CButton color="secondary" variant="outline" size="sm" @click="preview">
            <CIcon icon="cil-code" class="me-1" />{{ t('預覽腳本') }}
          </CButton>
        </CCardHeader>
        <CCardBody class="p-2">
          <ActionFlowGraph
            v-if="view === 'graph' && steps.length"
            :steps="steps"
            :selected-id="selectedId"
            @select="select"
            @connect="connect"
            @disconnect="disconnect"
          />
          <draggable
            v-if="view === 'list'"
            v-model="steps"
            group="actions"
            item-key="id"
            handle=".action-handle"
            class="action-flow"
          >
            <template #item="{ element, index }">
              <div
                class="action-step"
                :class="{ 'is-selected': element.id === selectedId, 'is-disabled': !element.enabled }"
                @click="select(element.id)"
              >
                <div class="d-flex align-items-center gap-2">
                  <span class="action-handle text-body-secondary" :title="t('拖曳調整順序')">⋮⋮</span>
                  <span class="badge text-bg-light border">{{ index + 1 }}</span>
                  <CIcon :icon="definitionOf(element)?.icon ?? 'cil-terminal'" class="text-body-secondary" />
                  <div class="me-auto">
                    <div class="fw-semibold">{{ titleOf(element) }}</div>
                    <div class="small text-body-secondary">{{ definitionOf(element)?.name ?? element.actionId }}</div>
                  </div>
                  <CBadge v-if="element.continueOnError" color="light" class="border text-body">
                    {{ t('失敗續行') }}
                  </CBadge>
                  <CBadge v-if="element.runWhen === 'always'" color="light" class="border text-body">{{ t('總是') }}</CBadge>
                  <CBadge v-if="element.runWhen === 'failure'" color="light" class="border text-body">{{ t('失敗時') }}</CBadge>
                  <CBadge v-if="element.retries" color="light" class="border text-body">{{ t('重試 {count} 次', { count: element.retries }) }}</CBadge>
                  <CBadge v-if="element.condition" color="light" class="border text-body font-monospace" :title="element.condition">{{ t('條件') }}</CBadge>
                  <CBadge v-if="element.key" color="light" class="border text-body font-monospace" :title="t('識別字')">{{ element.key }}</CBadge>
                  <CBadge v-if="dependencyLabels.has(element.id)" color="light" class="border text-body" :title="t('依賴步驟')">
                    ← {{ dependencyLabels.get(element.id) }}
                  </CBadge>
                  <CBadge v-if="!element.enabled" text-bg-color="secondary">{{ t('停用') }}</CBadge>
                </div>

                <div class="d-flex justify-content-end gap-1 mt-2" @click.stop>
                  <CButton color="secondary" variant="ghost" size="sm" :title="t('上移')" @click="move(element, -1)">↑</CButton>
                  <CButton color="secondary" variant="ghost" size="sm" :title="t('下移')" @click="move(element, 1)">↓</CButton>
                  <CButton color="secondary" variant="ghost" size="sm" :title="t('複製')" @click="duplicate(element)">
                    <CIcon icon="cil-copy" />
                  </CButton>
                  <CButton color="danger" variant="ghost" size="sm" :title="t('刪除')" @click="remove(element)">
                    <CIcon icon="cil-trash" />
                  </CButton>
                </div>
              </div>
            </template>
          </draggable>

          <div v-if="steps.length === 0" class="action-empty text-body-secondary">
            {{ t('從左邊把動作拖進來，或直接點一下動作加到流程尾端。') }}
          </div>
        </CCardBody>
      </CCard>
    </div>

    <div class="flow-side flow-side-right" :class="{ 'is-collapsed': settingsCollapsed }">
      <button v-if="settingsCollapsed" type="button" class="flow-strip" :title="t('展開步驟設定')" @click="toggleSettings">
        <span aria-hidden="true">‹</span>
        <CIcon icon="cil-settings" />
        <span class="flow-strip-label">{{ t('步驟設定') }}</span>
      </button>
      <CCard v-else class="h-100">
        <CCardHeader class="d-flex align-items-center gap-2">
          <span class="me-auto">{{ t('步驟設定') }}</span>
          <CButton color="secondary" variant="ghost" size="sm" :title="t('收合步驟設定')" @click="toggleSettings">›</CButton>
        </CCardHeader>
        <CCardBody>
          <ActionParameters
            v-if="selected && selectedDefinition"
            :step="selected"
            :definition="selectedDefinition"
            :references="references"
            :steps="steps"
            @update="(changes) => patch(selected!, changes)"
          />
          <p v-else class="text-body-secondary mb-0">{{ t('選一個步驟來設定細項。') }}</p>
        </CCardBody>
      </CCard>
    </div>
  </div>

  <ActionLibraryModal :visible="libraryVisible" @close="libraryVisible = false" />

  <CModal size="lg" scrollable :visible="previewVisible" @close="previewVisible = false">
    <CModalHeader>
      <CModalTitle>{{ t('預覽腳本') }}</CModalTitle>
    </CModalHeader>
    <CModalBody>
      <CAlert v-if="previewErrors.length" color="warning">
        <div v-for="(error, index) in previewErrors" :key="index">{{ error }}</div>
      </CAlert>
      <pre class="bg-body-tertiary border rounded p-3 mb-0"><code>{{ previewScript }}</code></pre>
    </CModalBody>
    <CModalFooter>
      <CButton color="primary" @click="previewVisible = false">{{ t('關閉') }}</CButton>
    </CModalFooter>
  </CModal>
</template>

<style scoped>
.flow-layout {
  display: flex;
  gap: 1rem;
  align-items: stretch;
}

.flow-main {
  flex: 1 1 auto;
  min-width: 0;
}

.flow-side {
  flex: 0 0 18rem;
  min-width: 0;
}

.flow-side-right {
  flex-basis: 22rem;
}

.flow-side.is-collapsed {
  flex-basis: 2.5rem;
}

/* The collapsed strip: vertical text, the whole strip is clickable. */
.flow-strip {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: .5rem;
  width: 2.5rem;
  height: 100%;
  min-height: 12rem;
  padding: .75rem 0;
  border: 1px solid var(--cui-border-color);
  border-radius: var(--cui-border-radius);
  background: var(--cui-card-bg, var(--cui-body-bg));
  color: var(--cui-body-color);
  cursor: pointer;
}

.flow-strip:hover {
  border-color: var(--cui-primary);
  color: var(--cui-primary);
}

.flow-strip-label {
  writing-mode: vertical-rl;
  letter-spacing: .1em;
  font-size: .875rem;
}

@media (max-width: 991.98px) {
  .flow-layout {
    flex-direction: column;
  }

  .flow-side,
  .flow-side.is-collapsed {
    flex-basis: auto;
  }

  .flow-strip {
    flex-direction: row;
    width: 100%;
    height: auto;
    min-height: 0;
    padding: .5rem .75rem;
    justify-content: center;
  }

  .flow-strip-label {
    writing-mode: horizontal-tb;
  }
}

.action-palette,
.action-flow {
  max-height: 28rem;
  overflow-y: auto;
}

.action-card {
  display: block;
  margin-bottom: .375rem;
  padding: .5rem .625rem;
  border: 1px solid var(--cui-border-color);
  border-radius: var(--cui-border-radius);
  background: var(--cui-body-bg);
  cursor: grab;
}

.action-card:hover {
  border-color: var(--cui-primary);
}

.action-step {
  margin-bottom: .5rem;
  padding: .625rem;
  border: 1px solid var(--cui-border-color);
  border-radius: var(--cui-border-radius);
  background: var(--cui-body-bg);
  cursor: pointer;
}

.action-step.is-selected {
  border-color: var(--cui-primary);
  box-shadow: 0 0 0 .15rem rgba(var(--cui-primary-rgb), .2);
}

.action-step.is-disabled {
  opacity: .6;
}

.action-handle {
  cursor: grab;
  letter-spacing: -.15em;
}

.action-empty {
  padding: 2.5rem 1rem;
  border: 1px dashed var(--cui-border-color);
  border-radius: var(--cui-border-radius);
  text-align: center;
}
</style>
