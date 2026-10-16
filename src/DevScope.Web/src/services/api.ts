import type { ApplicationSettings, CacheRefreshInterval, Employee, LlmModelCatalog, PeriodSummary, TaskAnalysis } from '../types'

async function getJson<T>(url: string): Promise<T> {
  const response = await fetch(url)
  if (!response.ok) throw new Error(await errorMessage(response))
  return response.json() as Promise<T>
}

async function errorMessage(response: Response): Promise<string> {
  try {
    const problem = await response.json() as { detail?: string; error?: string; title?: string }
    return problem.detail ?? problem.error ?? problem.title ?? `HTTP ${response.status}`
  } catch {
    return `HTTP ${response.status}`
  }
}

export const api = {
  employees: () => getJson<Employee[]>('/api/employees'),
  runtime: () => getJson<{ dataSource: string; isMock: boolean }>('/api/configuration/runtime'),
  llmModels: () => getJson<LlmModelCatalog>('/api/llm/models'),
  settings: () => getJson<ApplicationSettings>('/api/settings'),
  updateSettings: (defaultLlmModel: string, cacheRefreshInterval: CacheRefreshInterval) =>
    fetch('/api/settings', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ defaultLlmModel, cacheRefreshInterval })
    }).then(async response => {
      if (!response.ok) throw new Error(await errorMessage(response))
      return response.json() as Promise<ApplicationSettings>
    }),
  activity: (employeeId: string, from: string, to: string) =>
    getJson<PeriodSummary>(`/api/activity?employeeId=${encodeURIComponent(employeeId)}&from=${from}&to=${to}`),
  refreshActivity: (employeeId: string) =>
    fetch(`/api/activity/refresh?employeeId=${encodeURIComponent(employeeId)}`, { method: 'POST' })
      .then(async response => {
        if (!response.ok) throw new Error(await errorMessage(response))
        return response.json() as Promise<{ refreshedAt: string }>
      }),
  refreshAllActivity: () =>
    fetch('/api/activity/refresh/all', { method: 'POST' })
      .then(async response => {
        if (!response.ok) throw new Error(await errorMessage(response))
        return response.json() as Promise<ApplicationSettings>
      }),
  analyzeTask: (employeeId: string, taskId: string, model: string) =>
    fetch(`/api/tasks/${encodeURIComponent(taskId)}/analysis?employeeId=${encodeURIComponent(employeeId)}&model=${encodeURIComponent(model)}`, { method: 'POST' })
      .then(async response => {
        if (!response.ok) throw new Error(await errorMessage(response))
        return response.json() as Promise<TaskAnalysis>
      })
}

