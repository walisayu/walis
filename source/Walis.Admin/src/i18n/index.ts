import { createI18n } from 'vue-i18n'
import { en, ja, sourceGroups } from './messages'

/** The source locale; the dictionaries use the Traditional Chinese source text as keys, and untranslated strings show the source text. */
export const SOURCE_LOCALE = 'zh-Hant'

const STORAGE_KEY = 'walis-locale'

export { sourceGroups }

export const sourceStrings = Object.keys(sourceGroups)

/**
 * The source locale also needs a "source to source" dictionary: vue-i18n only compiles a message when the dictionary hits,
 * and without this mapping parameters like {name} are not substituted under Traditional Chinese.
 */
const source: Record<string, string> = Object.fromEntries(sourceStrings.map((text) => [text, text]))

/** The built-in dictionaries; the translation management page uses them to tell which strings have been overridden. */
export const builtInMessages: Record<string, Record<string, string>> = {
  [SOURCE_LOCALE]: source,
  en,
  ja
}

function storedLocale(): string {
  const stored = localStorage.getItem(STORAGE_KEY)
  return stored && stored in builtInMessages ? stored : SOURCE_LOCALE
}

export const i18n = createI18n({
  legacy: false,
  locale: storedLocale(),
  fallbackLocale: SOURCE_LOCALE,
  messages: {
    [SOURCE_LOCALE]: { ...source },
    en: { ...en },
    ja: { ...ja }
  },
  // The source text itself is the key and may contain ".", so vue-i18n's path resolution must be turned off.
  messageResolver: (message, key) => (message as Record<string, string>)[key] ?? null,
  missingWarn: false,
  fallbackWarn: false,
  warnHtmlMessage: false
})

const htmlLang: Record<string, string> = { [SOURCE_LOCALE]: 'zh-Hant', en: 'en', ja: 'ja' }

export function setLocale(code: string): void {
  i18n.global.locale.value = code as typeof i18n.global.locale.value
  localStorage.setItem(STORAGE_KEY, code)
  document.documentElement.lang = htmlLang[code] ?? code
}

export function currentLocale(): string {
  return i18n.global.locale.value as string
}

/** Layers the translation overrides stored by the backend over the built-in dictionaries. */
export function applyOverrides(overrides: Record<string, Record<string, string>>): void {
  for (const code of Object.keys(builtInMessages)) {
    i18n.global.setLocaleMessage(code, {
      ...builtInMessages[code],
      ...(overrides[code] ?? {})
    })
  }
}

export function translate(source: string, values?: Record<string, unknown>): string {
  return values ? i18n.global.t(source, values) : i18n.global.t(source)
}

setLocale(storedLocale())
