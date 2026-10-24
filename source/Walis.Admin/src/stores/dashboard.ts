import { defineStore } from 'pinia'
import { ref } from 'vue'
import { api } from '../services/api'
import type { DashboardSummary } from '../types'

export const useDashboardStore = defineStore('dashboard', () => {
  const summary = ref<DashboardSummary | null>(null)
  const loading = ref(false)

  async function load(): Promise<void> {
    loading.value = true

    try {
      summary.value = await api.dashboard()
    } finally {
      loading.value = false
    }
  }

  return { summary, loading, load }
})
