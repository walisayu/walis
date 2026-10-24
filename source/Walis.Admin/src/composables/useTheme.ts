import { readonly, ref } from 'vue'

export type ThemeMode = 'light' | 'dark' | 'auto'

const STORAGE_KEY = 'walis-theme'
const media = window.matchMedia('(prefers-color-scheme: dark)')
const mode = ref<ThemeMode>(readStored())

function readStored(): ThemeMode {
  const stored = localStorage.getItem(STORAGE_KEY)
  return stored === 'light' || stored === 'dark' || stored === 'auto' ? stored : 'auto'
}

function apply(value: ThemeMode): void {
  const resolved = value === 'auto' ? (media.matches ? 'dark' : 'light') : value
  document.documentElement.setAttribute('data-coreui-theme', resolved)
  document.documentElement.dispatchEvent(new Event('ColorSchemeChange'))
}

media.addEventListener('change', () => {
  if (mode.value === 'auto') {
    apply('auto')
  }
})

apply(mode.value)

export function useTheme() {
  function setTheme(value: ThemeMode): void {
    mode.value = value
    localStorage.setItem(STORAGE_KEY, value)
    apply(value)
  }

  return { mode: readonly(mode), setTheme }
}
