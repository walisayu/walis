import type { ActionStep } from '../types'

/**
 * The flow's dependency graph. When no step in the flow sets dependsOn (old data) it is derived from order and the "parallel" flag:
 * each step depends on every step of the previous group, and consecutive steps marked parallel form one group. This matches the backend's BuildStepGraph.DeriveLinear.
 */
export function isExplicit(steps: ActionStep[]): boolean {
  return steps.some((step) => step.dependsOn !== undefined && step.dependsOn !== null)
}

/** The step ids each step really depends on (derived or explicit). */
export function dependenciesOf(steps: ActionStep[]): Map<string, string[]> {
  const result = new Map<string, string[]>()
  const ids = new Set(steps.map((step) => step.id))

  if (isExplicit(steps)) {
    for (const step of steps) {
      result.set(step.id, (step.dependsOn ?? []).filter((id) => id !== step.id && ids.has(id)))
    }
    return result
  }

  let previousGroup: string[] = []
  let currentGroup: string[] = []
  steps.forEach((step, index) => {
    if (index > 0 && !step.parallel) {
      previousGroup = currentGroup
      currentGroup = []
    }
    result.set(step.id, [...previousGroup])
    currentGroup.push(step.id)
  })
  return result
}

/** Writes the derived dependencies back into every step, after which the flow is an explicit graph. An already explicit flow is returned as is. */
export function makeExplicit(steps: ActionStep[]): ActionStep[] {
  if (isExplicit(steps)) {
    return steps
  }
  const dependencies = dependenciesOf(steps)
  return steps.map((step) => ({ ...step, dependsOn: dependencies.get(step.id) ?? [] }))
}

/** All downstream steps of a step (transitively), used to block dependencies that would create a cycle. */
export function descendantsOf(steps: ActionStep[], id: string): Set<string> {
  const dependencies = dependenciesOf(steps)
  const children = new Map<string, string[]>()
  for (const [child, parents] of dependencies) {
    for (const parent of parents) {
      children.set(parent, [...(children.get(parent) ?? []), child])
    }
  }
  const result = new Set<string>()
  const stack = [id]
  while (stack.length) {
    const current = stack.pop()!
    for (const child of children.get(current) ?? []) {
      if (!result.has(child)) {
        result.add(child)
        stack.push(child)
      }
    }
  }
  return result
}

/** All upstream steps of a step (transitively). */
export function ancestorsOf(steps: ActionStep[], id: string): Set<string> {
  const dependencies = dependenciesOf(steps)
  const result = new Set<string>()
  const stack = [id]
  while (stack.length) {
    const current = stack.pop()!
    for (const parent of dependencies.get(current) ?? []) {
      if (!result.has(parent)) {
        result.add(parent)
        stack.push(parent)
      }
    }
  }
  return result
}

/** Topological order (stable: prefers the earlier original position when there is a choice); on a cycle the remainder is appended at the end. */
export function topologicalOrder(steps: ActionStep[]): ActionStep[] {
  const dependencies = dependenciesOf(steps)
  const remaining = new Map(steps.map((step) => [step.id, new Set(dependencies.get(step.id) ?? [])]))
  const byId = new Map(steps.map((step) => [step.id, step]))
  const order: ActionStep[] = []
  while (remaining.size) {
    const ready = steps.filter((step) => remaining.has(step.id) && remaining.get(step.id)!.size === 0)
    if (!ready.length) {
      for (const id of remaining.keys()) {
        order.push(byId.get(id)!)
      }
      break
    }
    for (const step of ready) {
      order.push(step)
      remaining.delete(step.id)
    }
    for (const set of remaining.values()) {
      for (const step of ready) {
        set.delete(step.id)
      }
    }
  }
  return order
}
