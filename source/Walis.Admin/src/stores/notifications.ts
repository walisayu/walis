import { defineStore } from 'pinia'
import { ref } from 'vue'
import { api } from '../services/api'
import type { NotificationChannel, NotificationLogEntry } from '../types'

export const useNotificationsStore = defineStore('notifications', () => {
  const channels = ref<NotificationChannel[]>([])
  const log = ref<NotificationLogEntry[]>([])
  const loading = ref(false)

  async function load(): Promise<void> {
    loading.value = true

    try {
      const [channelList, logEntries] = await Promise.all([api.notificationChannels(), api.notificationLog()])
      channels.value = channelList
      log.value = logEntries
    } finally {
      loading.value = false
    }
  }

  async function update(channel: NotificationChannel): Promise<void> {
    await api.updateNotificationChannel(channel.id, {
      kind: channel.kind,
      name: channel.name,
      target: channel.target,
      isEnabled: channel.isEnabled
    })
    await load()
  }

  async function test(id: string): Promise<NotificationLogEntry> {
    const entry = await api.testNotificationChannel(id)
    await load()
    return entry
  }

  return { channels, log, loading, load, update, test }
})
