import { defineStore } from 'pinia'
import { ref } from 'vue'

export type ToastColor = 'primary' | 'secondary' | 'success' | 'danger' | 'warning' | 'info' | 'dark'

export interface Toast {
  id: number
  message: string
  color: ToastColor
}

let nextId = 1

export const useToastStore = defineStore('toasts', () => {
  const items = ref<Toast[]>([])

  function push(message: string, color: ToastColor = 'dark'): void {
    items.value.push({ id: nextId++, message, color })
  }

  function remove(id: number): void {
    items.value = items.value.filter((toast) => toast.id !== id)
  }

  const success = (message: string) => push(message, 'success')
  const error = (message: string) => push(message, 'danger')
  const info = (message: string) => push(message, 'primary')

  return { items, push, remove, success, error, info }
})
