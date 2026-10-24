<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'

// Replaces CoreUI's CSearchButton (5.10.x), which calls event.key.toLowerCase() on every document keydown/keyup.
// Chrome's password autofill dispatches keyboard events without a key, so pages with credential forms threw
// "Cannot read properties of undefined (reading 'toLowerCase')". The markup and classes match CoreUI's styles.
const emit = defineEmits<{ trigger: [] }>()

type NavigatorWithPlatform = Navigator & { userAgentData?: { platform?: string } }
const nav = navigator as NavigatorWithPlatform
const isMac = /Mac|iPhone|iPad|iPod|macOS|Macintosh/.test(
  nav.userAgentData?.platform || nav.platform || nav.userAgent || ''
)

const COMMAND = '⌘'
const CONTROL = 'Ctrl'
const SHORTCUT_KEY = '/'
const tokens = [isMac ? COMMAND : CONTROL, SHORTCUT_KEY]
const activeKeys = ref<string[]>([])

function pressedKeys(event: KeyboardEvent): string[] {
  const keys: string[] = []
  if (event.ctrlKey) keys.push(CONTROL)
  if (event.metaKey) keys.push(COMMAND)
  if (event.type === 'keydown' && typeof event.key === 'string' && event.key.length === 1) keys.push(event.key)
  return keys
}

function onKeydown(event: KeyboardEvent): void {
  activeKeys.value = pressedKeys(event)

  // ⌘/ on macOS, Ctrl+/ elsewhere; both are accepted like CoreUI's default "meta+/,ctrl+/".
  if (event.defaultPrevented || event.repeat || typeof event.key !== 'string') return
  if (event.key !== SHORTCUT_KEY || event.altKey || event.shiftKey || event.ctrlKey === event.metaKey) return

  event.preventDefault()
  emit('trigger')
}

function onKeyup(event: KeyboardEvent): void {
  activeKeys.value = pressedKeys(event)
}

function onBlur(): void {
  activeKeys.value = []
}

onMounted(() => {
  document.addEventListener('keydown', onKeydown)
  document.addEventListener('keyup', onKeyup)
  window.addEventListener('blur', onBlur)
})

onBeforeUnmount(() => {
  document.removeEventListener('keydown', onKeydown)
  document.removeEventListener('keyup', onKeyup)
  window.removeEventListener('blur', onBlur)
})
</script>

<template>
  <button type="button" class="search-button" @click="emit('trigger')">
    <slot />
    <span class="search-button-keys" aria-hidden="true">
      <span
        v-for="key in tokens"
        :key="key"
        class="search-button-key"
        :class="{ active: activeKeys.includes(key) }"
      >{{ key }}</span>
    </span>
  </button>
</template>
