import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { api } from '../services/api'
import type { AiProvider, AiProviderInput, AiRequestLogEntry } from '../types'

export const useAiStore = defineStore('ai', () => {
  const providers = ref<AiProvider[]>([])
  const requests = ref<AiRequestLogEntry[]>([])
  const loading = ref(false)
  const loaded = ref(false)

  const usable = computed(() => providers.value.filter((provider) => provider.isUsable))
  const isReady = computed(() => usable.value.length > 0)

  async function load(force = false): Promise<void> {
    if (loaded.value && !force) {
      return
    }

    loading.value = true

    try {
      providers.value = await api.aiProviders()
      loaded.value = true
    } finally {
      loading.value = false
    }
  }

  async function loadRequests(): Promise<void> {
    requests.value = await api.aiRequests()
  }

  async function update(id: string, payload: AiProviderInput): Promise<void> {
    await api.updateAiProvider(id, payload)
    await load(true)
  }

  async function test(id: string): Promise<string> {
    const result = await api.testAiProvider(id)
    await loadRequests()
    return result.text
  }

  return { providers, requests, loading, loaded, usable, isReady, load, loadRequests, update, test }
})
