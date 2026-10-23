import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { api } from '../services/api'
import type { ActionDefinition, ActionPackList, ActionStep } from '../types'

export const useActionsStore = defineStore('actions', () => {
  const definitions = ref<ActionDefinition[]>([])
  const categories = ref<string[]>([])
  const loading = ref(false)
  const loaded = ref(false)

  const byId = computed(() => new Map(definitions.value.map((definition) => [definition.id, definition])))
  const custom = computed(() => definitions.value.filter((definition) => !definition.isBuiltIn && !definition.source))
  const packs = ref<ActionPackList>({ roots: [], packs: [] })

  async function load(force = false): Promise<void> {
    if (loaded.value && !force) {
      return
    }

    loading.value = true

    try {
      const catalog = await api.actionCatalog()
      definitions.value = catalog.definitions
      categories.value = catalog.categories
      loaded.value = true
    } finally {
      loading.value = false
    }
  }

  async function preview(steps: ActionStep[]) {
    return api.previewActions(steps)
  }

  async function install(manifest: string): Promise<ActionDefinition> {
    const definition = await api.installAction(manifest)
    await load(true)
    return definition
  }

  async function loadPacks(): Promise<void> {
    packs.value = await api.actionPacks()
  }

  async function reloadPacks(): Promise<void> {
    packs.value = await api.reloadActionPacks()
    await load(true)
  }

  async function uninstall(id: string): Promise<void> {
    await api.uninstallAction(id)
    await load(true)
  }

  /** Creates a step with default values from an action definition. */
  function createStep(definition: ActionDefinition): ActionStep {
    const parameters: Record<string, string | null> = {}

    for (const parameter of definition.parameters) {
      parameters[parameter.name] = parameter.default ?? (parameter.type === 'boolean' ? 'false' : '')
    }

    return {
      id: crypto.randomUUID(),
      actionId: definition.id,
      name: null,
      key: null,
      enabled: true,
      continueOnError: false,
      parallel: false,
      runWhen: 'success',
      retries: 0,
      parameters
    }
  }

  return {
    definitions,
    categories,
    loading,
    loaded,
    byId,
    custom,
    packs,
    load,
    loadPacks,
    reloadPacks,
    preview,
    install,
    uninstall,
    createStep
  }
})
