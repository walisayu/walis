<script setup lang="ts">
import { computed } from 'vue'
import { icons } from '../icons'

/**
 * Replaces CIcon from @coreui/icons-vue: that version writes the string "undefined" into every SVG
 * when no title is given, polluting the text and the accessibility tree.
 */
const props = defineProps<{
  icon: string
  size?: 'sm' | 'lg' | 'xl' | 'xxl' | '3xl'
  customClassName?: string
  title?: string
}>()

const definition = computed<string[] | undefined>(() => {
  const key = props.icon.replace(/-([a-z0-9])/g, (_, character: string) => character.toUpperCase())
  const catalog = icons as Record<string, string[] | undefined>
  const candidate = Object.hasOwn(catalog, key) ? catalog[key] : undefined

  // Plugin/action metadata controls the icon name, never the SVG body. Only a
  // genuine own entry from the statically bundled CoreUI catalog may reach the
  // v-html sink below.
  return Array.isArray(candidate) && candidate.every((part) => typeof part === 'string') ? candidate : undefined
})

const viewBox = computed(() => {
  const value = definition.value
  return value && value.length > 1 ? `0 0 ${value[0]}` : '0 0 512 512'
})

const content = computed(() => {
  const value = definition.value

  if (!value) {
    return ''
  }

  return value.length > 1 ? value[1] : value[0]
})

const classes = computed(() =>
  props.customClassName ? props.customClassName : ['icon', props.size ? `icon-${props.size}` : ''].filter(Boolean)
)
</script>

<template>
  <svg
    xmlns="http://www.w3.org/2000/svg"
    :class="classes"
    :viewBox="viewBox"
    role="img"
    :aria-hidden="title ? undefined : true"
  >
    <title v-if="title">{{ title }}</title>
    <!-- Icon bodies come only from the statically imported CoreUI catalog. -->
    <g v-html="content" />
  </svg>
</template>
