import { defineStore } from 'pinia'
import { ref } from 'vue'
import { api } from '../services/api'
import { SOURCE_LOCALE, applyOverrides, builtInMessages, sourceGroups, sourceStrings } from '../i18n'
import type { Locale } from '../types'

export interface TranslationProgress {
  total: number
  done: number
  percent: number
}

export const useLocalizationStore = defineStore('localization', () => {
  const locales = ref<Locale[]>([])
  const overrides = ref<Record<string, Record<string, string>>>({})
  const loading = ref(false)
  const loaded = ref(false)

  async function load(force = false): Promise<void> {
    if (loaded.value && !force) {
      return
    }

    loading.value = true

    try {
      const payload = await api.localization()
      locales.value = payload.locales
      overrides.value = payload.overrides
      applyOverrides(payload.overrides)
      loaded.value = true
    } finally {
      loading.value = false
    }
  }

  /** The translations currently in effect: database overrides first, then the built-in dictionary. */
  function valueOf(locale: string, source: string): string {
    return overrides.value[locale]?.[source] ?? builtInMessages[locale]?.[source] ?? ''
  }

  function isOverridden(locale: string, source: string): boolean {
    return overrides.value[locale]?.[source] !== undefined
  }

  function progress(code: string): TranslationProgress {
    const total = sourceStrings.length

    if (code === SOURCE_LOCALE) {
      return { total, done: total, percent: 100 }
    }

    const done = sourceStrings.filter((source) => valueOf(code, source).length > 0).length
    return { total, done, percent: total === 0 ? 100 : Math.round((done / total) * 100) }
  }

  function groupOf(source: string): string {
    return sourceGroups[source] ?? '共用'
  }

  async function saveTranslation(locale: string, source: string, value: string): Promise<void> {
    const trimmed = value.trim()
    await api.saveTranslation(locale, source, trimmed)

    const current = { ...(overrides.value[locale] ?? {}) }

    if (trimmed.length === 0) {
      delete current[source]
    } else {
      current[source] = trimmed
    }

    overrides.value = { ...overrides.value, [locale]: current }
    applyOverrides(overrides.value)
  }

  /** Uses the English dictionary as the source for machine translation to fill in untranslated strings. */
  async function autoTranslate(locale: string): Promise<number> {
    const entries: Record<string, string> = {}

    for (const source of sourceStrings) {
      if (valueOf(locale, source).length === 0) {
        entries[source] = builtInMessages.en[source] ?? source
      }
    }

    const count = Object.keys(entries).length

    if (count === 0) {
      return 0
    }

    await api.saveTranslations(locale, entries)
    await load(true)
    return count
  }

  async function reset(locale: string): Promise<number> {
    const { cleared } = await api.clearTranslations(locale)
    await load(true)
    return cleared
  }

  function exportJson(locale: string): string {
    const payload: Record<string, string> = {}

    for (const source of sourceStrings) {
      const value = valueOf(locale, source)

      if (value.length > 0) {
        payload[source] = value
      }
    }

    return JSON.stringify(payload, null, 2)
  }

  async function importJson(locale: string, json: string): Promise<number> {
    const parsed = JSON.parse(json) as Record<string, string>
    const entries: Record<string, string> = {}

    for (const [source, value] of Object.entries(parsed)) {
      if (typeof value === 'string' && value.trim().length > 0) {
        entries[source] = value.trim()
      }
    }

    const count = Object.keys(entries).length

    if (count === 0) {
      return 0
    }

    await api.saveTranslations(locale, entries)
    await load(true)
    return count
  }

  return {
    locales,
    overrides,
    loading,
    loaded,
    load,
    valueOf,
    isOverridden,
    progress,
    groupOf,
    saveTranslation,
    autoTranslate,
    reset,
    exportJson,
    importJson
  }
})
