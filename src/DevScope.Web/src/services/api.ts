import type { Employee, LlmModelCatalog, PeriodSummary, TaskAnalysis } from '../types'

async function getJson<T>(url: string): Promise<T> {
  const response = await fetch(url)
  if (!response.ok) throw new Error(`HTTP ${response.status}`)
  return response.json() as Promise<T>
}

export const api = {
  employees: () => getJson<Employee[]>('/api/employees'),
  llmModels: () => getJson<LlmModelCatalog>('/api/llm/models'),
  activity: (employeeId: string, from: string, to: string) =>
    getJson<PeriodSummary>(`/api/activity?employeeId=${encodeURIComponent(employeeId)}&from=${from}&to=${to}`),
  analyzeTask: (employeeId: string, taskId: string, model: string) =>
    fetch(`/api/tasks/${encodeURIComponent(taskId)}/analysis?employeeId=${encodeURIComponent(employeeId)}&model=${encodeURIComponent(model)}`, { method: 'POST' })
      .then(async response => {
        if (!response.ok) throw new Error(`HTTP ${response.status}`)
        return response.json() as Promise<TaskAnalysis>
      })
}

