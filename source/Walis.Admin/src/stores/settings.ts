import { defineStore } from 'pinia'
import { ref } from 'vue'
import { api } from '../services/api'
import type { SettingsPayload, WalisSettings } from '../types'

export const useSettingsStore = defineStore('settings', () => {
  const settings = ref<WalisSettings | null>(null)
  const databasePath = ref('')
  const timeZones = ref<string[]>([])
  const version = ref('')
  const sandboxAvailable = ref(true)
  const loading = ref(false)

  async function load(force = false): Promise<void> {
    if (settings.value && !force) {
      return
    }

    loading.value = true

    try {
      const payload: SettingsPayload = await api.settings()
      settings.value = payload.settings
      databasePath.value = payload.databasePath ?? ''
      timeZones.value = payload.timeZones
      version.value = payload.version ?? ''
      sandboxAvailable.value = payload.sandboxAvailable ?? true
    } finally {
      loading.value = false
    }
  }

  async function save(value: WalisSettings): Promise<void> {
    settings.value = await api.saveSettings(value)
  }

  return { settings, databasePath, timeZones, version, sandboxAvailable, loading, load, save }
})
