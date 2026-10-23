import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { api } from '../services/api'
import type { Build, Job, JobInput } from '../types'

export const useJobsStore = defineStore('jobs', () => {
  const items = ref<Job[]>([])
  const loading = ref(false)
  const loaded = ref(false)

  const enabled = computed(() => items.value.filter((job) => !job.isDisabled))
  const scheduled = computed(() => items.value.filter((job) => job.triggerKind === 'cron' && job.cron))

  async function load(force = false): Promise<void> {
    if (loaded.value && !force) {
      return
    }

    loading.value = true

    try {
      items.value = await api.jobs()
      loaded.value = true
    } finally {
      loading.value = false
    }
  }

  function byId(id: string): Job | undefined {
    return items.value.find((job) => job.id === id)
  }

  function replace(job: Job): void {
    const index = items.value.findIndex((item) => item.id === job.id)

    if (index === -1) {
      items.value = [...items.value, job]
    } else {
      items.value[index] = job
    }
  }

  async function create(payload: JobInput): Promise<Job> {
    const job = await api.createJob(payload)
    replace(job)
    return job
  }

  async function update(id: string, payload: JobInput): Promise<Job> {
    const job = await api.updateJob(id, payload)
    replace(job)
    return job
  }

  async function setEnabled(id: string, value: boolean): Promise<Job> {
    const job = await api.setJobEnabled(id, value)
    replace(job)
    return job
  }

  async function remove(id: string): Promise<void> {
    await api.deleteJob(id)
    items.value = items.value.filter((job) => job.id !== id)
  }

  async function run(id: string, parameters?: Record<string, string>): Promise<Build> {
    const build = await api.runJob(id, 'manual', parameters)
    await load(true)
    return build
  }

  return { items, loading, loaded, enabled, scheduled, load, byId, create, update, setEnabled, remove, run }
})
