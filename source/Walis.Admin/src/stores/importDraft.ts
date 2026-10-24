import { defineStore } from 'pinia'
import { ref } from 'vue'
import type { JenkinsEnvironmentVariable, JenkinsJobDraft } from '../types'

/** The draft imported from Jenkins: the import dialog puts it here, the job editor takes it in create mode, and it is cleared after one use. */
export const useImportDraftStore = defineStore('importDraft', () => {
  const job = ref<JenkinsJobDraft | null>(null)
  const environment = ref<JenkinsEnvironmentVariable[]>([])

  function set(draft: JenkinsJobDraft, variables: JenkinsEnvironmentVariable[]): void {
    job.value = draft
    environment.value = variables
  }

  function take(): { job: JenkinsJobDraft; environment: JenkinsEnvironmentVariable[] } | null {
    if (!job.value) {
      return null
    }

    const taken = { job: job.value, environment: environment.value }
    job.value = null
    environment.value = []
    return taken
  }

  return { job, environment, set, take }
})
