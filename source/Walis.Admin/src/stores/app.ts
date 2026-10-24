import { defineStore } from 'pinia'
import { ref } from 'vue'

export const useAppStore = defineStore('app', () => {
  const sidebarVisible = ref(true)
  const sidebarUnfoldable = ref(false)
  const ready = ref(false)
  const connectionError = ref<string | null>(null)

  function setSidebarVisible(value: boolean): void {
    sidebarVisible.value = value
  }

  function toggleUnfoldable(): void {
    sidebarUnfoldable.value = !sidebarUnfoldable.value
  }

  return { sidebarVisible, sidebarUnfoldable, ready, connectionError, setSidebarVisible, toggleUnfoldable }
})
