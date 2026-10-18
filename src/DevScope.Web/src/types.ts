export interface Employee {
  id: string
  displayName: string
  team: string
  role: string
}

export interface Worklog {
  id: string
  taskId: string
  authorId: string
  date: string
  durationMinutes: number
  type: string
  description?: string
  url?: string
}

export interface GitLabReference {
  projectPath: string
  branch: string
  mergeRequestIid: number
  mergeRequestState: string
  commitCount: number
  linkSource: string
  discussionCount?: number
  url?: string
  employeeApproved?: boolean
  employeeCommentCount?: number
  employeeCommitCount?: number
  employeeIsReviewer?: boolean
  commitAuthorsKnown?: boolean
  otherCommitAuthors?: string[]
}

export interface TaskActivity {
  task: {
    id: string
    summary: string
    status: string
    developerId?: string
    estimateMinutes?: number
    url: string
    gitLab?: GitLabReference
    digitalTwinStarted: boolean
  }
  loggedMinutes: number
  worklogs: Worklog[]
  isOwnTask: boolean
}

export interface ActivityEvent {
  id: string
  taskId: string
  occurredAt: string
  source: 'YouTrack' | 'GitLab'
  kind: string
  title: string
  details?: string
  url?: string
}

export interface PeriodSummary {
  employee: Employee
  from: string
  to: string
  loggedMinutes: number
  taskCount: number
  mergeRequestCount: number
  narrative: string
  tasks: TaskActivity[]
  events: ActivityEvent[]
  limitations: string[]
  dataUpdatedAt?: string
  hasExactWorklogDurations: boolean
}

export interface TaskAnalysis {
  taskId: string
  context: string
  type: string
  taskSummary: string
  employeeContribution: string
  result: string
  model: string
  generatedAt: string
}

export interface LlmModelOption {
  id: string
  label: string
  description: string
  recommended: boolean
}

export interface LlmModelCatalog {
  defaultModel: string
  models: LlmModelOption[]
}

export type CacheRefreshInterval = 'Hourly' | 'Daily'

export interface ApplicationSettings {
  defaultLlmModel: string
  cacheRefreshInterval: CacheRefreshInterval
  lastCacheRefreshAt?: string
  cachedEmployeeCount: number
}

