import { defineStore } from 'pinia'
import { ref } from 'vue'
import { api } from '../services/api'
import type { Runner, RunnerInput } from '../types'

export const useRunnersStore = defineStore('runners', () => {
  const items = ref<Runner[]>([])
  const loading = ref(false)
  const loaded = ref(false)

  async function load(force = false): Promise<void> {
    if (loaded.value && !force) {
      return
    }

    loading.value = true

    try {
      items.value = await api.runners()
      loaded.value = true
    } finally {
      loading.value = false
    }
  }

  async function create(payload: RunnerInput): Promise<Runner> {
    const runner = await api.createRunner(payload)
    await load(true)
    return runner
  }

  async function update(id: string, payload: RunnerInput): Promise<Runner> {
    const runner = await api.updateRunner(id, payload)
    await load(true)
    return runner
  }

  async function setPaused(id: string, paused: boolean): Promise<Runner> {
    const runner = await api.setRunnerPaused(id, paused)
    await load(true)
    return runner
  }

  async function remove(id: string): Promise<number> {
    const result = await api.deleteRunner(id)
    await load(true)
    return result?.disabledKeyCount ?? 0
  }

  return { items, loading, loaded, load, create, update, setPaused, remove }
})
