<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Handle, MarkerType, Position, VueFlow, useVueFlow } from '@vue-flow/core'
import type { Connection, Edge, EdgeMouseEvent, Node, NodeMouseEvent } from '@vue-flow/core'
import dagre from '@dagrejs/dagre'
import { useActionsStore } from '../../stores/actions'
import type { ActionStep } from '../../types'
import { dependenciesOf, descendantsOf } from '../../utils/stepGraph'

const props = defineProps<{ steps: ActionStep[]; selectedId: string | null }>()
const emit = defineEmits<{
  (event: 'select', id: string): void
  (event: 'connect', from: string, to: string): void
  (event: 'disconnect', from: string, to: string): void
}>()

const { t } = useI18n()
const actions = useActionsStore()
const { fitView } = useVueFlow()

const NODE_WIDTH = 220
const NODE_HEIGHT = 64

interface StepNodeData {
  step: ActionStep
  title: string
  icon: string
}

// Node's generics make vue-tsc expand without end, so the node array uses a loose type and the template reads data as StepNodeData.
const nodes = ref<Node[]>([])
const edges = ref<Edge[]>([])
const layoutError = ref('')

function titleOf(step: ActionStep): string {
  return step.name?.trim() || actions.byId.get(step.actionId)?.name || step.actionId
}

/** Lays out with dagre: left to right, dependencies on the left. Positions are not saved and are recomputed from the graph every time. */
function layout(): void {
  const dependencies = dependenciesOf(props.steps)
  // dagre's generic types make vue-tsc expand without end, so this is narrowed to the few methods used.
  const graph = new dagre.graphlib.Graph() as unknown as {
    setDefaultEdgeLabel(fn: () => object): void
    setGraph(options: object): void
    setNode(id: string, options: { width: number; height: number }): void
    setEdge(from: string, to: string): void
    node(id: string): { x: number; y: number } | undefined
  }
  graph.setDefaultEdgeLabel(() => ({}))
  graph.setGraph({ rankdir: 'LR', nodesep: 24, ranksep: 64, marginx: 16, marginy: 16 })
  for (const step of props.steps) {
    graph.setNode(step.id, { width: NODE_WIDTH, height: NODE_HEIGHT })
  }
  const nextEdges: Edge[] = []
  for (const [id, parents] of dependencies) {
    for (const parent of parents) {
      graph.setEdge(parent, id)
      nextEdges.push({
        id: `${parent}->${id}`,
        source: parent,
        target: id,
        markerEnd: MarkerType.ArrowClosed,
        class: 'flow-edge'
      })
    }
  }
  try {
    ;(dagre as unknown as { layout(graph: unknown): void }).layout(graph)
    layoutError.value = ''
  } catch {
    layoutError.value = t('依賴有循環，圖沒有辦法排版；請在步驟設定裡拿掉其中一條依賴。')
  }

  const nextNodes: Node[] = []
  props.steps.forEach((step, index) => {
    const placed = graph.node(step.id)
    const data: StepNodeData = { step, title: titleOf(step), icon: actions.byId.get(step.actionId)?.icon ?? 'cil-terminal' }
    nextNodes.push({
      id: step.id,
      type: 'step',
      position: placed ? { x: placed.x - NODE_WIDTH / 2, y: placed.y - NODE_HEIGHT / 2 } : { x: 0, y: index * (NODE_HEIGHT + 16) },
      data,
      class: step.id === props.selectedId ? 'is-selected-node' : '',
      draggable: true,
      connectable: true
    })
  })
  nodes.value = nextNodes
  edges.value = nextEdges
  void nextTick(() => fitView({ padding: 0.2 }))
}

const signature = computed(() =>
  props.steps
    .map((step) => `${step.id}|${step.name ?? ''}|${step.key ?? ''}|${step.enabled}|${step.parallel}|${step.condition ?? ''}|${(step.dependsOn ?? ['-']).join(',')}`)
    .join(';')
)
watch(signature, layout, { immediate: true })
watch(
  () => props.selectedId,
  (id) => {
    nodes.value = nodes.value.map((node) => ({ ...node, class: node.id === id ? 'is-selected-node' : '' }))
  }
)

function onConnect(connection: Connection): void {
  const from = connection.source
  const to = connection.target
  if (!from || !to || from === to) {
    return
  }
  if (descendantsOf(props.steps, to).has(from)) {
    layoutError.value = t('這條依賴會造成循環，已略過。')
    return
  }
  emit('connect', from, to)
}

function onEdgeClick(event: EdgeMouseEvent): void {
  emit('disconnect', event.edge.source, event.edge.target)
}

function onNodeClick(event: NodeMouseEvent): void {
  emit('select', event.node.id)
}
</script>

<template>
  <div class="flow-graph">
    <CAlert v-if="layoutError" color="warning" class="py-1 px-2 small mb-2">{{ layoutError }}</CAlert>
    <VueFlow
      v-model:nodes="nodes"
      v-model:edges="edges"
      :min-zoom="0.3"
      :max-zoom="1.5"
      :nodes-draggable="true"
      :edges-updatable="false"
      :delete-key-code="null"
      :connection-radius="30"
      fit-view-on-init
      @connect="onConnect"
      @edge-click="onEdgeClick"
      @node-click="onNodeClick"
    >
      <template #node-step="{ data }">
        <div class="flow-node" :class="{ 'is-selected': data.step.id === selectedId, 'is-disabled': !data.step.enabled }">
          <Handle type="target" :position="Position.Left" class="flow-handle" />
          <div class="d-flex align-items-center gap-2">
            <CIcon :icon="data.icon" class="text-body-secondary flex-shrink-0" />
            <div class="flex-grow-1 min-w-0">
              <div class="fw-semibold text-truncate">{{ data.title }}</div>
              <div class="small text-body-secondary text-truncate">
                <code v-if="data.step.key">{{ data.step.key }}</code>
                <span v-if="data.step.runWhen === 'always'"> · {{ t('總是') }}</span>
                <span v-if="data.step.runWhen === 'failure'"> · {{ t('失敗時') }}</span>
                <span v-if="data.step.continueOnError"> · {{ t('失敗續行') }}</span>
                <span v-if="data.step.condition" :title="data.step.condition"> · {{ t('條件') }}</span>
                <span v-if="!data.step.enabled"> · {{ t('停用') }}</span>
              </div>
            </div>
          </div>
          <Handle type="source" :position="Position.Right" class="flow-handle" />
        </div>
      </template>
    </VueFlow>
    <div class="small text-body-secondary mt-1">
      {{ t('從節點右側的圓點拖到另一個節點就是加一條依賴；點一下連線就拿掉。沒有連線的步驟會同時開始。') }}
    </div>
  </div>
</template>

<style>
@import '@vue-flow/core/dist/style.css';
@import '@vue-flow/core/dist/theme-default.css';

.flow-graph {
  height: 28rem;
  display: flex;
  flex-direction: column;
}

.flow-graph .vue-flow {
  flex: 1 1 auto;
  border: 1px solid var(--cui-border-color);
  border-radius: var(--cui-border-radius);
  background: var(--cui-body-bg);
}

.flow-node {
  width: 220px;
  min-height: 64px;
  padding: .5rem .75rem;
  border: 1px solid var(--cui-border-color);
  border-radius: var(--cui-border-radius);
  background: var(--cui-card-bg, var(--cui-body-bg));
  color: var(--cui-body-color);
  box-shadow: 0 1px 2px rgba(0, 0, 0, .06);
}

.flow-node.is-selected {
  border-color: var(--cui-primary);
  box-shadow: 0 0 0 .15rem rgba(var(--cui-primary-rgb), .25);
}

.flow-node.is-disabled {
  opacity: .55;
}

.flow-handle {
  width: 10px;
  height: 10px;
  background: var(--cui-primary);
  border: 2px solid var(--cui-card-bg, var(--cui-body-bg));
}

.vue-flow__edge.flow-edge path {
  stroke: var(--cui-secondary-color);
  stroke-width: 1.5;
  cursor: pointer;
}

.vue-flow__edge.flow-edge:hover path {
  stroke: var(--cui-danger);
}
</style>
