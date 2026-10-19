<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import Button from 'primevue/button'
import DatePicker from 'primevue/datepicker'
import Select from 'primevue/select'
import Skeleton from 'primevue/skeleton'
import { CalendarDays, ChartNoAxesColumnIncreasing, CheckCircle2, ChevronDown, Clock3, Database, ExternalLink, GitMerge, Info, RefreshCw, Save, Search, Settings, Sparkles, UserCheck, UserRound } from '@lucide/vue'
import { api } from './services/api'
import type { ApplicationSettings, CacheRefreshInterval, Employee, GitLabReference, LlmModelOption, PeriodSummary, TaskActivity, TaskAnalysis } from './types'

const employees = ref<Employee[]>([])
const selectedEmployee = ref<string>('')
const today = new Date()
const mondayOffset = (today.getDay() + 6) % 7
const currentMonday = new Date(today.getFullYear(), today.getMonth(), today.getDate() - mondayOffset)
const dates = ref<Date[]>([currentMonday, new Date(currentMonday.getFullYear(), currentMonday.getMonth(), currentMonday.getDate() + 6)])
const summary = ref<PeriodSummary | null>(null)
const loading = ref(true)
const error = ref('')
const analyzingTask = ref('')
const taskAnalyses = ref<Record<string, TaskAnalysis>>({})
const taskAnalysisErrors = ref<Record<string, string>>({})
const llmModels = ref<LlmModelOption[]>([])
const selectedModel = ref('smart')
const dataSource = ref('Mock')
const isMockRuntime = ref(true)
const activePage = ref<'activity' | 'settings'>('activity')
const applicationSettings = ref<ApplicationSettings | null>(null)
const cacheRefreshInterval = ref<CacheRefreshInterval>('Daily')
const settingsBusy = ref(false)
const cacheRefreshBusy = ref(false)
const settingsMessage = ref('')
const settingsError = ref('')
const timelineSource = ref<'All' | 'YouTrack' | 'GitLab'>('All')
const expandedTimelineEvents = ref<Record<string, boolean>>({})
const expandedTaskWorklogs = ref<Record<string, boolean>>({})
const showAllTimelineEvents = ref(false)
const showReviewQueue = ref(false)
const collapsedWorklogLimit = 3

const refreshIntervalOptions = [
  { label: 'Раз в час', value: 'Hourly' as CacheRefreshInterval },
  { label: 'Раз в день', value: 'Daily' as CacheRefreshInterval }
]

const employeeOptions = computed(() => employees.value.map(item => ({ label: item.displayName, value: item.id })))
const selectedEmployeeDetails = computed(() => employees.value.find(item => item.id === selectedEmployee.value))
const totalHours = computed(() => summary.value ? formatDuration(summary.value.loggedMinutes) : '0 ч')
const activeDays = computed(() => new Set(summary.value?.tasks.flatMap(task => task.worklogs.map(item => item.date)) ?? []).size)
const worklogPresenceCount = computed(() => summary.value?.tasks.reduce((sum, task) => sum + task.worklogs.length, 0) ?? 0)
const currentReviewAssignments = computed(() => summary.value?.currentReviewAssignments ?? [])
const reviewHours = computed(() => {
  const minutes = summary.value?.tasks.flatMap(task => task.worklogs)
    .filter(worklog => worklog.type.toLocaleLowerCase().includes('ревью'))
    .reduce((sum, worklog) => sum + worklog.durationMinutes, 0) ?? 0
  return formatDuration(minutes)
})
const filteredTimelineEvents = computed(() => {
  const events = summary.value?.events ?? []
  return timelineSource.value === 'All' ? events : events.filter(event => event.source === timelineSource.value)
})
const visibleTimelineEvents = computed(() => showAllTimelineEvents.value
  ? filteredTimelineEvents.value
  : filteredTimelineEvents.value.slice(0, 14))
const hiddenTimelineEventCount = computed(() => Math.max(0, filteredTimelineEvents.value.length - visibleTimelineEvents.value.length))
const periodLabel = computed(() => {
  if (dates.value.length < 2 || !dates.value[1]) return ''
  return `${formatDateOnly(toIsoDate(dates.value[0]))} — ${formatDateOnly(toIsoDate(dates.value[1]))}`
})

function toIsoDate(value: Date) {
  const year = value.getFullYear()
  const month = String(value.getMonth() + 1).padStart(2, '0')
  const day = String(value.getDate()).padStart(2, '0')
  return `${year}-${month}-${day}`
}

function formatDuration(minutes: number) {
  const hours = Math.floor(minutes / 60)
  const rest = minutes % 60
  return rest ? `${hours} ч ${rest} мин` : `${hours} ч`
}

function formatDateOnly(value: string) {
  const [year, month, day] = value.split('-')
  return `${day}.${month}.${year}`
}

function pluralizeCommits(count: number) {
  const mod100 = count % 100
  const mod10 = count % 10
  if (mod100 >= 11 && mod100 <= 14) return `${count} коммитов`
  if (mod10 === 1) return `${count} коммит`
  if (mod10 >= 2 && mod10 <= 4) return `${count} коммита`
  return `${count} коммитов`
}

function reviewQueueLabel(count: number) {
  const mod100 = count % 100
  const mod10 = count % 10
  if (mod100 >= 11 && mod100 <= 14) return `${count} открытых MR`
  if (mod10 === 1) return `${count} открытый MR`
  return `${count} открытых MR`
}

function formatCommitActivity(gitLab: GitLabReference) {
  if (gitLab.commitCount === 0) return 'Коммитов в MR нет'
  if (!gitLab.commitAuthorsKnown) return `${pluralizeCommits(gitLab.commitCount)} в MR · авторы не определены`

  const employeeCount = gitLab.employeeCommitCount ?? 0
  const otherCount = Math.max(0, gitLab.commitCount - employeeCount)
  if (employeeCount > 0 && otherCount > 0) {
    return `${pluralizeCommits(employeeCount)} сотрудника · ${pluralizeCommits(otherCount)} от других авторов`
  }
  if (employeeCount > 0) return `${pluralizeCommits(employeeCount)} сотрудника в MR`
  return `${pluralizeCommits(otherCount)} от других авторов в MR`
}

function formatMergeRequestState(state: string) {
  const labels: Record<string, string> = {
    open: 'Открыт', opened: 'Открыт', merged: 'Слит', closed: 'Закрыт', locked: 'Заблокирован'
  }
  return labels[state.trim().toLocaleLowerCase()] ?? ''
}

function hasConfirmedGitLabAction(gitLab: GitLabReference) {
  return (gitLab.employeeCommitCount ?? 0) > 0 ||
    Boolean(gitLab.employeeCreatedMergeRequest) ||
    Boolean(gitLab.employeeMergedMergeRequest) ||
    Boolean(gitLab.employeeApproved) ||
    (gitLab.employeeCommentCount ?? 0) > 0 ||
    Boolean(gitLab.employeeResolvedThreads)
}

function cleanEventDetails(value?: string) {
  if (!value) return ''
  return value
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&amp;/g, '&')
    .replace(/<\/?(?:ul|ol|li|p|br)[^>]*>/gi, match => /<\/(?:li|p)>|<br/i.test(match) ? '\n' : '')
    .replace(/<[^>]+>/g, '')
    .replace(/\[([^\]]+)]\([^)]+\)/g, '$1')
    .replace(/\n{3,}/g, '\n\n')
    .trim()
}

function formatEventTime(occurredAt: string, kind: string) {
  const value = new Date(occurredAt)
  if (kind === 'Worklog' || kind === 'WorklogPresence') {
    return value.toLocaleDateString('ru-RU', { day: '2-digit', month: 'short' })
  }
  return value.toLocaleString('ru-RU', { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' })
}

function timelineDetailsAreLong(value?: string) {
  return cleanEventDetails(value).length > 180 || cleanEventDetails(value).split('\n').length > 3
}

function toggleTimelineEvent(eventId: string) {
  expandedTimelineEvents.value = {
    ...expandedTimelineEvents.value,
    [eventId]: !expandedTimelineEvents.value[eventId]
  }
}

function toggleTaskWorklogs(taskId: string) {
  expandedTaskWorklogs.value = {
    ...expandedTaskWorklogs.value,
    [taskId]: !expandedTaskWorklogs.value[taskId]
  }
}

function isReview(task: TaskActivity) {
  return task.worklogs.some(item => item.type.toLocaleLowerCase().includes('ревью'))
}

function setThisWeek() {
  dates.value = [currentMonday, new Date(currentMonday.getFullYear(), currentMonday.getMonth(), currentMonday.getDate() + 6)]
}

function setPreviousWeek() {
  dates.value = [
    new Date(currentMonday.getFullYear(), currentMonday.getMonth(), currentMonday.getDate() - 7),
    new Date(currentMonday.getFullYear(), currentMonday.getMonth(), currentMonday.getDate() - 1)
  ]
}

async function load() {
  if (!selectedEmployee.value || dates.value.length < 2 || !dates.value[1]) return
  loading.value = true
  error.value = ''
  timelineSource.value = 'All'
  expandedTimelineEvents.value = {}
  expandedTaskWorklogs.value = {}
  showAllTimelineEvents.value = false
  showReviewQueue.value = false
  try {
    summary.value = await api.activity(selectedEmployee.value, toIsoDate(dates.value[0]), toIsoDate(dates.value[1]))
  } catch (exception) {
    error.value = exception instanceof Error ? exception.message : 'Не удалось загрузить данные'
  } finally {
    loading.value = false
  }
}

function changeEmployee() {
  taskAnalyses.value = {}
  taskAnalysisErrors.value = {}
  void load()
}

async function saveSettings() {
  settingsBusy.value = true
  settingsError.value = ''
  settingsMessage.value = ''
  try {
    applicationSettings.value = await api.updateSettings(selectedModel.value, cacheRefreshInterval.value)
    settingsMessage.value = 'Настройки сохранены.'
    taskAnalyses.value = {}
    taskAnalysisErrors.value = {}
  } catch (exception) {
    settingsError.value = exception instanceof Error ? exception.message : 'Не удалось сохранить настройки'
  } finally {
    settingsBusy.value = false
  }
}

async function refreshAllCache() {
  cacheRefreshBusy.value = true
  settingsError.value = ''
  settingsMessage.value = ''
  try {
    applicationSettings.value = await api.refreshAllActivity()
    settingsMessage.value = 'Кэш YouTrack и GitLab обновлён.'
    await load()
  } catch (exception) {
    settingsError.value = exception instanceof Error ? exception.message : 'Не удалось обновить кэш'
  } finally {
    cacheRefreshBusy.value = false
  }
}

async function analyzeTask(taskId: string) {
  if (!selectedEmployee.value || analyzingTask.value) return
  analyzingTask.value = taskId
  taskAnalysisErrors.value = { ...taskAnalysisErrors.value, [taskId]: '' }
  try {
    const analysis = await api.analyzeTask(selectedEmployee.value, taskId, selectedModel.value)
    taskAnalyses.value = { ...taskAnalyses.value, [taskId]: analysis }
  } catch (exception) {
    taskAnalysisErrors.value = {
      ...taskAnalysisErrors.value,
      [taskId]: exception instanceof Error ? exception.message : 'Не удалось выполнить анализ'
    }
  } finally {
    analyzingTask.value = ''
  }
}

onMounted(async () => {
  try {
    const [employeeList, modelCatalog, runtime, settings] = await Promise.all([api.employees(), api.llmModels(), api.runtime(), api.settings()])
    employees.value = employeeList
    llmModels.value = modelCatalog.models
    dataSource.value = runtime.dataSource
    isMockRuntime.value = runtime.isMock
    applicationSettings.value = settings
    selectedModel.value = settings.defaultLlmModel
    cacheRefreshInterval.value = settings.cacheRefreshInterval
    selectedEmployee.value = employees.value[0]?.id ?? ''
    await load()
  } catch (exception) {
    error.value = exception instanceof Error ? exception.message : 'Не удалось загрузить сотрудников'
    loading.value = false
  }
})

watch(dates, value => {
  if (value.length === 2 && value[1]) void load()
}, { deep: true })

watch(selectedModel, value => {
  taskAnalyses.value = {}
  taskAnalysisErrors.value = {}
})
</script>

<template>
  <div class="shell">
    <aside class="sidebar">
      <div class="brand"><span class="brand-mark"><ChartNoAxesColumnIncreasing :size="23" /></span><strong>DevScope</strong></div>
      <nav>
        <button :class="{ active: activePage === 'activity' }" @click="activePage = 'activity'"><UserRound :size="19" />Работа сотрудника</button>
      </nav>
      <nav class="sidebar-bottom"><button :class="{ active: activePage === 'settings' }" @click="activePage = 'settings'"><Settings :size="19" />Настройки</button></nav>
    </aside>

    <main>
      <div class="sticky-top">
      <header>
        <div>
          <p class="eyebrow">Fintender · Обзор деятельности</p>
          <h1>{{ activePage === 'activity' ? 'Работа сотрудника' : 'Настройки' }}</h1>
        </div>
        <div class="header-actions">
          <span class="mode"><Database :size="15" /> {{ isMockRuntime ? 'Mock MCP' : 'Real MCP' }}</span>
        </div>
      </header>

      <section v-if="activePage === 'activity'" class="filters card">
        <label><span>Сотрудник</span><Select v-model="selectedEmployee" :options="employeeOptions" optionLabel="label" optionValue="value" @change="changeEmployee" /></label>
        <label><span>Период</span><DatePicker v-model="dates" selectionMode="range" dateFormat="dd.mm.yy" :manualInput="false" showIcon /></label>
        <div class="quick"><Button label="Прошлая неделя" severity="secondary" outlined @click="setPreviousWeek" /><Button label="Эта неделя" severity="secondary" outlined @click="setThisWeek" /></div>
      </section>
      </div>

      <template v-if="activePage === 'activity'">
      <div v-if="error" class="error">{{ error }}</div>
      <template v-if="loading">
        <Skeleton height="9rem" borderRadius="12px" />
        <div class="columns"><Skeleton height="34rem" borderRadius="12px" /><Skeleton height="34rem" borderRadius="12px" /></div>
      </template>

      <template v-else-if="summary">
        <section class="summary card">
          <div class="summary-copy"><span class="round-icon"><Search :size="21" /></span><div><div class="summary-person"><h2>{{ selectedEmployeeDetails?.displayName }}</h2><span>{{ periodLabel }}</span></div><p>{{ summary.narrative }}</p><small v-if="summary.dataUpdatedAt">Данные обновлены {{ new Date(summary.dataUpdatedAt).toLocaleString('ru-RU') }}</small></div></div>
          <div v-if="summary.hasExactWorklogDurations" class="metric"><span>Списано по worklog</span><strong>{{ totalHours }}</strong></div>
          <div v-else class="metric"><span>Дней со списаниями</span><strong>{{ activeDays }}</strong></div>
          <div v-if="summary.hasExactWorklogDurations" class="metric"><span>На Code Review</span><strong>{{ reviewHours }}</strong></div>
          <div v-else class="metric"><span>Подтверждений списаний</span><strong>{{ worklogPresenceCount }}</strong></div>
          <div class="metric"><span>Задач в выборке</span><strong>{{ summary.taskCount }}</strong></div>
          <div class="metric"><span>Связанных MR</span><strong>{{ summary.mergeRequestCount }}</strong></div>
        </section>

        <div v-if="!summary.hasExactWorklogDurations" class="source-notice">
          <Info :size="19" />
          <div>
            <strong>MCP подтверждает дни списаний, но не возвращает их длительность</strong>
            <span>Показываем только подтверждённые факты: сотрудник был автором worklog в указанный день. Часы не рассчитываются из полей задачи или активности GitLab.</span>
          </div>
        </div>

        <section class="review-queue card">
          <div class="review-queue-summary">
            <div class="review-queue-title">
              <span class="review-queue-icon"><UserCheck :size="19" /></span>
              <div><h2>Сейчас ожидают ревью</h2><small>Текущее состояние открытых MR, не относится к выбранному периоду</small></div>
            </div>
            <div class="review-queue-actions">
              <strong>{{ reviewQueueLabel(currentReviewAssignments.length) }}</strong>
              <button v-if="currentReviewAssignments.length" class="review-queue-toggle" :aria-expanded="showReviewQueue" @click="showReviewQueue = !showReviewQueue">
                {{ showReviewQueue ? 'Скрыть' : 'Подробнее' }}
                <ChevronDown :size="15" :class="{ rotated: showReviewQueue }" />
              </button>
            </div>
          </div>
          <div v-if="showReviewQueue" class="review-queue-details">
            <div v-for="item in currentReviewAssignments" :key="`${item.task.id}:${item.task.gitLab?.mergeRequestIid ?? 0}`" class="review-queue-item">
              <div>
                <a v-if="item.task.url" :href="item.task.url" target="_blank" rel="noopener noreferrer">{{ item.task.id }}</a>
                <strong v-else>{{ item.task.id }}</strong>
                <span>{{ item.task.summary }}</span>
              </div>
              <span class="mr-state">{{ formatMergeRequestState(item.task.gitLab?.mergeRequestState ?? '') || item.task.gitLab?.mergeRequestState }}</span>
              <a v-if="item.task.gitLab?.url" class="mr-link" :href="item.task.gitLab.url" target="_blank" rel="noopener noreferrer">MR !{{ item.task.gitLab.mergeRequestIid }}<ExternalLink :size="13" /></a>
            </div>
          </div>
        </section>

        <div class="columns">
          <section class="card tasks">
            <div class="section-title"><div><span class="yt">YT</span><div><h2>Активность по задачам</h2><small>Worklog или подтверждённое действие сотрудника в GitLab за выбранный период</small></div></div></div>
            <article v-for="item in summary.tasks" :key="item.task.id" class="task-card" :class="{ review: isReview(item) }">
              <div class="task-head">
                <div class="task-title">
                  <h3 :title="item.task.summary"><a :href="item.task.url" target="_blank" rel="noopener noreferrer">{{ item.task.id }}</a> · {{ item.task.summary }}</h3>
                  <div class="task-signals">
                    <span v-if="item.task.digitalTwinStarted" class="digital-twin" title="В задаче есть тег SODA в YouTrack">Цифровой Двойник</span>
                    <span v-if="!item.isOwnTask" class="foreign" title="Задача попала в выборку по списанию сотрудника, даже если исполнителем назначен другой человек">Автор списания, не исполнитель</span>
                  </div>
                </div>
                <span class="status">{{ item.task.status }}</span>
              </div>
              <div v-if="item.task.estimateMinutes !== undefined && item.task.estimateMinutes !== null" class="execution-estimate">
                <ChartNoAxesColumnIncreasing :size="17" />
                <span>Оценка на исполнение</span>
                <strong>{{ formatDuration(item.task.estimateMinutes) }}</strong>
              </div>
              <div v-if="item.worklogs.length" class="worklog-total"><Clock3 :size="18" /><strong>{{ summary.hasExactWorklogDurations ? formatDuration(item.loggedMinutes) : item.worklogs.length }}</strong><span>{{ summary.hasExactWorklogDurations ? 'списал выбранный сотрудник' : 'дн. с подтверждёнными списаниями' }}</span></div>
              <div v-else class="worklog-total reviewer-only"><UserCheck :size="18" /><span>Подтверждена активность в GitLab · списаний в выбранном периоде нет</span></div>
              <div v-if="item.worklogs.length" class="worklogs">
                <div class="worklog-header" :class="{ presence: !summary.hasExactWorklogDurations }">
                  <span>Дата</span><span v-if="summary.hasExactWorklogDurations">Тип</span><span v-if="summary.hasExactWorklogDurations">Время</span><span>Подтверждение</span><span></span>
                </div>
                <div v-for="worklog in (expandedTaskWorklogs[item.task.id] ? item.worklogs : item.worklogs.slice(0, collapsedWorklogLimit))" :key="worklog.id" :class="{ presence: !summary.hasExactWorklogDurations }">
                  <span>{{ formatDateOnly(worklog.date) }}</span><strong v-if="summary.hasExactWorklogDurations">{{ worklog.type }}</strong><span v-if="summary.hasExactWorklogDurations">{{ formatDuration(worklog.durationMinutes) }}</span><em>{{ summary.hasExactWorklogDurations ? worklog.description : 'Автор и дата подтверждены YouTrack' }}</em>
                  <a v-if="worklog.url" class="row-link" :href="worklog.url" target="_blank" rel="noopener noreferrer" title="Открыть это списание в YouTrack"><ExternalLink :size="14" /><span>Открыть</span></a>
                </div>
                <button v-if="item.worklogs.length > collapsedWorklogLimit" class="worklogs-toggle" @click="toggleTaskWorklogs(item.task.id)">
                  {{ expandedTaskWorklogs[item.task.id] ? 'Скрыть' : `Ещё ${item.worklogs.length - collapsedWorklogLimit}` }}
                  <ChevronDown :size="14" :class="{ rotated: expandedTaskWorklogs[item.task.id] }" />
                </button>
              </div>
              <div v-if="item.task.gitLab" class="gitlab-row">
                <div class="gitlab-header">
                  <div class="gitlab-identity"><span class="gitlab-icon"><GitMerge :size="17" /></span><strong>GitLab</strong><span v-if="item.task.gitLab.branch" class="branch" :title="item.task.gitLab.branch">{{ item.task.gitLab.branch }}</span></div>
                  <a v-if="item.task.gitLab.url" class="mr-link" :href="item.task.gitLab.url" target="_blank" rel="noopener noreferrer">MR !{{ item.task.gitLab.mergeRequestIid }}<ExternalLink :size="14" /></a>
                  <span v-else class="mr-unavailable">Ссылка на MR недоступна</span>
                </div>
                <div class="gitlab-overview">
                  <span class="gitlab-commit-summary">{{ formatCommitActivity(item.task.gitLab) }}</span>
                  <span class="mr-state">Статус: {{ formatMergeRequestState(item.task.gitLab.mergeRequestState) || item.task.gitLab.mergeRequestState }}</span>
                </div>
                <div v-if="item.task.gitLab.commitAuthorsKnown && item.task.gitLab.otherCommitAuthors?.length" class="commit-authors"><span>Другие авторы</span><strong>{{ item.task.gitLab.otherCommitAuthors.join(', ') }}</strong></div>
                <div class="gitlab-signals">
                  <span v-if="item.task.gitLab.employeeIsReviewer" class="gitlab-signal reviewer"><UserCheck :size="14" />Ревьюер MR</span>
                  <span v-if="item.task.gitLab.employeeCreatedMergeRequest" class="gitlab-signal">Создал MR</span>
                  <span v-if="item.task.gitLab.employeeMergedMergeRequest" class="gitlab-signal approved"><CheckCircle2 :size="14" />Слил MR</span>
                  <span v-if="item.task.gitLab.employeeApproved" class="gitlab-signal approved"><CheckCircle2 :size="14" />Поставил аппрув</span>
                  <span v-if="item.task.gitLab.employeeCommentCount" class="gitlab-signal">Комментарии: {{ item.task.gitLab.employeeCommentCount }}</span>
                  <span v-if="item.task.gitLab.employeeResolvedThreads" class="gitlab-signal">Разрешил треды</span>
                </div>
                <span v-if="item.task.gitLab.employeeActionFactsKnown && !hasConfirmedGitLabAction(item.task.gitLab)" class="gitlab-no-action">Подтверждённых действий сотрудника в MR не найдено</span>
              </div>
              <div class="analysis-action">
                <Button
                  severity="secondary"
                  outlined
                  size="small"
                  :loading="analyzingTask === item.task.id"
                  :disabled="Boolean(analyzingTask) && analyzingTask !== item.task.id"
                  @click="analyzeTask(item.task.id)"
                >
                  <Sparkles :size="16" />
                  <span>{{ taskAnalyses[item.task.id] ? 'Обновить резюме' : 'Сформировать резюме' }}</span>
                </Button>
                <small>ИИ запускается только для этой задачи</small>
              </div>
              <div v-if="taskAnalysisErrors[item.task.id]" class="analysis-error">{{ taskAnalysisErrors[item.task.id] }}</div>
              <section v-if="taskAnalyses[item.task.id]" class="task-analysis">
                <div class="analysis-heading"><Sparkles :size="18" /><strong>Объяснение задачи</strong></div>
                <div class="analysis-context">
                  <span>{{ taskAnalyses[item.task.id].context }}</span>
                  <strong>{{ taskAnalyses[item.task.id].type }}</strong>
                </div>
                <dl>
                  <div><dt>Задача</dt><dd>{{ taskAnalyses[item.task.id].taskSummary }}</dd></div>
                  <div><dt>В MR</dt><dd>{{ taskAnalyses[item.task.id].mergeSummary }}</dd></div>
                  <div><dt>Статус и проверка</dt><dd>{{ taskAnalyses[item.task.id].statusAndChecks }}</dd></div>
                </dl>
              </section>
            </article>
          </section>

          <section class="card timeline">
            <div class="section-title timeline-title"><div><span class="round-icon"><CalendarDays :size="20" /></span><div><h2>Хронология</h2><small>{{ filteredTimelineEvents.length }} событий</small></div></div></div>
            <div class="timeline-filters" role="group" aria-label="Источник событий">
              <button v-for="source in (['All', 'YouTrack', 'GitLab'] as const)" :key="source" :class="{ active: timelineSource === source }" @click="timelineSource = source; showAllTimelineEvents = false">
                {{ source === 'All' ? 'Все' : source }}
              </button>
            </div>
            <div class="timeline-note"><Info :size="14" /><span>Worklog и GitLab показаны отдельно; события GitLab не переводятся в часы.</span></div>
            <div v-if="visibleTimelineEvents.length === 0" class="timeline-empty">Нет событий выбранного источника.</div>
            <div v-for="event in visibleTimelineEvents" :key="event.id" class="timeline-item">
              <span class="dot" :class="event.source.toLowerCase()"></span>
              <div class="timeline-content">
                <div class="timeline-meta"><time>{{ formatEventTime(event.occurredAt, event.kind) }}</time><span class="source" :class="event.source.toLowerCase()">{{ event.source }}</span></div>
                <div class="timeline-heading">
                  <a v-if="event.url" :href="event.url" target="_blank" rel="noopener noreferrer"><strong>{{ event.title }}</strong><ExternalLink :size="13" /></a>
                  <strong v-else>{{ event.title }}</strong>
                </div>
                <p v-if="event.details" :class="{ clamped: timelineDetailsAreLong(event.details) && !expandedTimelineEvents[event.id] }">{{ cleanEventDetails(event.details) }}</p>
                <button v-if="timelineDetailsAreLong(event.details)" class="details-toggle" @click="toggleTimelineEvent(event.id)">
                  {{ expandedTimelineEvents[event.id] ? 'Свернуть' : 'Подробнее' }}<ChevronDown :size="14" :class="{ rotated: expandedTimelineEvents[event.id] }" />
                </button>
              </div>
            </div>
            <button v-if="hiddenTimelineEventCount" class="show-more" @click="showAllTimelineEvents = true">Показать ещё {{ hiddenTimelineEventCount }}</button>
          </section>
        </div>
      </template>
      </template>

      <template v-else>
        <div v-if="settingsError" class="error">{{ settingsError }}</div>
        <div v-if="settingsMessage" class="success">{{ settingsMessage }}</div>
        <section class="settings-grid">
          <article class="card settings-card">
            <div class="settings-card-title"><span class="round-icon"><Sparkles :size="20" /></span><div><h2>Модель LLM</h2><p>Одна модель используется для полного объяснения задачи.</p></div></div>
            <label class="settings-field">
              <span>Модель по умолчанию</span>
              <Select v-model="selectedModel" :options="llmModels" optionLabel="label" optionValue="id" />
              <small>{{ llmModels.find(model => model.id === selectedModel)?.description }}</small>
            </label>
          </article>

          <article class="card settings-card">
            <div class="settings-card-title"><span class="round-icon"><Database :size="20" /></span><div><h2>Кэш данных</h2><p>PostgreSQL хранит результаты запросов YouTrack и GitLab. Интерфейс читает готовый снимок и не вызывает MCP при каждом открытии.</p></div></div>
            <label class="settings-field">
              <span>Автоматическое обновление</span>
              <Select v-model="cacheRefreshInterval" :options="refreshIntervalOptions" optionLabel="label" optionValue="value" />
              <small v-if="applicationSettings?.lastCacheRefreshAt">Последнее обновление: {{ new Date(applicationSettings.lastCacheRefreshAt).toLocaleString('ru-RU') }}</small>
              <small v-else>Кэш ещё не заполнялся.</small>
            </label>
            <div class="cache-meta">
              <span><strong>{{ applicationSettings?.cachedEmployeeCount ?? 0 }}</strong> сотрудников в кэше</span>
              <span>YouTrack: задачи и дни списаний</span>
              <span>GitLab: MR, ветки, коммиты и обсуждения</span>
            </div>
            <Button severity="secondary" outlined :loading="cacheRefreshBusy" @click="refreshAllCache"><RefreshCw :size="16" /><span>Обновить сейчас</span></Button>
          </article>
        </section>
        <div class="settings-save"><Button :loading="settingsBusy" @click="saveSettings"><Save :size="16" /><span>Сохранить настройки</span></Button></div>
      </template>
    </main>
  </div>
</template>

