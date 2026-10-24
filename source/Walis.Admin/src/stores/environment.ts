import { defineStore } from 'pinia'
import { ref } from 'vue'
import { api } from '../services/api'
import type { EnvironmentVariable, EnvironmentVariableInput } from '../types'

export const useEnvironmentStore = defineStore('environment', () => {
  const items = ref<EnvironmentVariable[]>([])
  const loading = ref(false)
  const reveal = ref(false)

  function clearSensitive(): void {
    items.value = []
    reveal.value = false
  }

  async function load(): Promise<void> {
    // Never leave values from a previous account visible while a request is in
    // flight.  This is especially important for revealed secrets: a 401/403
    // must fail closed instead of keeping the old plaintext in Pinia.
    items.value = []
    loading.value = true

    try {
      items.value = await api.environmentVariables(reveal.value)
    } catch (error) {
      clearSensitive()
      throw error
    } finally {
      loading.value = false
    }
  }

  async function setReveal(value: boolean): Promise<void> {
    items.value = []
    reveal.value = value
    await load()
  }

  async function create(payload: EnvironmentVariableInput): Promise<void> {
    await api.createEnvironmentVariable(payload)
    await load()
  }

  async function update(id: string, payload: EnvironmentVariableInput): Promise<void> {
    await api.updateEnvironmentVariable(id, payload)
    await load()
  }

  async function remove(id: string): Promise<void> {
    await api.deleteEnvironmentVariable(id)
    await load()
  }

  return { items, loading, reveal, clearSensitive, load, setReveal, create, update, remove }
})
