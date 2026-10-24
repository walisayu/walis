import type { BuildParameterDefinition, Job } from '../types'

/** The build parameters the job declares; a broken declaration is treated as none, and the backend validates again at trigger time. */
export function parseParameterDefinitions(job: Pick<Job, 'parametersJson'> | null | undefined): BuildParameterDefinition[] {
  if (!job?.parametersJson) {
    return []
  }

  try {
    const parsed = JSON.parse(job.parametersJson)
    return Array.isArray(parsed) ? parsed : []
  } catch {
    return []
  }
}

/** The parameter values recorded on the build (excluding the password type, which is never returned). */
export function parseParameterValues(json: string | null | undefined): [string, string][] {
  if (!json) {
    return []
  }

  try {
    const parsed = JSON.parse(json)
    return parsed && typeof parsed === 'object' ? Object.entries(parsed as Record<string, string>) : []
  } catch {
    return []
  }
}
