<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import Button from 'primevue/button'
import DatePicker from 'primevue/datepicker'
import Select from 'primevue/select'
import Skeleton from 'primevue/skeleton'
import { Activity, CalendarDays, ChartNoAxesColumnIncreasing, CheckCircle2, Clock3, Database, GitMerge, ListTodo, RefreshCw, Search, Settings, Sparkles, UserRound, UsersRound } from '@lucide/vue'
import { api } from './services/api'
import type { Employee, LlmModelOption, PeriodSummary, TaskActivity, TaskAnalysis } from './types'

const employees = ref<Employee[]>([])
const selectedEmployee = ref<string>('')
const dates = ref<Date[]>([new Date(2026, 9, 6), new Date(2026, 9, 12)])
const summary = ref<PeriodSummary | null>(null)
const loading = ref(true)
const error = ref('')
const analyzingTask = ref('')
const taskAnalyses = ref<Record<string, TaskAnalysis>>({})
const taskAnalysisErrors = ref<Record<string, string>>({})
const llmModels = ref<LlmModelOption[]>([])
const selectedModel = ref(localStorage.getItem('devscope.llmModel') ?? 'basic')

const employeeOptions = computed(() => employees.value.map(item => ({ label: item.displayName, value: item.id })))
const totalHours = computed(() => summary.value ? formatDuration(summary.value.loggedMinutes) : '0 ч')
const reviewHours = computed(() => {
  const minutes = summary.value?.tasks.flatMap(task => task.worklogs)
    .filter(worklog => worklog.type.toLocaleLowerCase().includes('ревью'))
    .reduce((sum, worklog) => sum + worklog.durationMinutes, 0) ?? 0
  return formatDuration(minutes)
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

function isReview(task: TaskActivity) {
  return task.worklogs.some(item => item.type.toLocaleLowerCase().includes('ревью'))
}

function setThisWeek() {
  dates.value = [new Date(2026, 9, 12), new Date(2026, 9, 18)]
}

function setPreviousWeek() {
  dates.value = [new Date(2026, 9, 5), new Date(2026, 9, 11)]
}

async function load() {
  if (!selectedEmployee.value || dates.value.length < 2 || !dates.value[1]) return
  loading.value = true
  error.value = ''
  try {
    summary.value = await api.activity(selectedEmployee.value, toIsoDate(dates.value[0]), toIsoDate(dates.value[1]))
  } catch (exception) {
    error.value = exception instanceof Error ? exception.message : 'Не удалось загрузить данные'
  } finally {
    loading.value = false
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
    const [employeeList, modelCatalog] = await Promise.all([api.employees(), api.llmModels()])
    employees.value = employeeList
    llmModels.value = modelCatalog.models
    if (!llmModels.value.some(model => model.id === selectedModel.value)) {
      selectedModel.value = modelCatalog.defaultModel
    }
    selectedEmployee.value = employees.value[0]?.id ?? ''
    await load()
  } catch (exception) {
    error.value = exception instanceof Error ? exception.message : 'Не удалось загрузить сотрудников'
    loading.value = false
  }
})

watch(selectedEmployee, () => {
  taskAnalyses.value = {}
  taskAnalysisErrors.value = {}
  void load()
})

watch(selectedModel, value => {
  localStorage.setItem('devscope.llmModel', value)
  taskAnalyses.value = {}
  taskAnalysisErrors.value = {}
})
</script>

<template>
  <div class="shell">
    <aside class="sidebar">
      <div class="brand"><span class="brand-mark"><ChartNoAxesColumnIncreasing :size="23" /></span><strong>DevScope</strong></div>
      <nav>
        <a class="active"><UserRound :size="19" />Работа сотрудника</a>
        <a><ListTodo :size="19" />Задачи</a>
        <a><Activity :size="19" />Активность</a>
        <a><UsersRound :size="19" />Команды</a>
      </nav>
      <nav class="sidebar-bottom"><a><Settings :size="19" />Настройки</a></nav>
    </aside>

    <main>
      <header>
        <div>
          <p class="eyebrow">Fintender · Обзор деятельности</p>
          <h1>Работа сотрудника</h1>
        </div>
        <div class="header-actions">
          <label class="model-picker">
            <span>Модель LLM</span>
            <Select v-model="selectedModel" :options="llmModels" optionLabel="label" optionValue="id" />
          </label>
          <span class="mode"><Database :size="15" /> Mock MCP</span>
        </div>
      </header>

      <section class="filters card">
        <label><span>Сотрудник</span><Select v-model="selectedEmployee" :options="employeeOptions" optionLabel="label" optionValue="value" /></label>
        <label><span>Период</span><DatePicker v-model="dates" selectionMode="range" dateFormat="dd.mm.yy" :manualInput="false" showIcon /></label>
        <div class="quick"><Button label="Прошлая неделя" severity="secondary" outlined @click="setPreviousWeek" /><Button label="Эта неделя" severity="secondary" outlined @click="setThisWeek" /></div>
        <Button @click="load"><RefreshCw :size="16" /><span>Обновить</span></Button>
      </section>

      <div v-if="error" class="error">{{ error }}</div>
      <template v-if="loading">
        <Skeleton height="9rem" borderRadius="12px" />
        <div class="columns"><Skeleton height="34rem" borderRadius="12px" /><Skeleton height="34rem" borderRadius="12px" /></div>
      </template>

      <template v-else-if="summary">
        <section class="summary card">
          <div class="summary-copy"><span class="round-icon"><Search :size="21" /></span><div><h2>Кратко за период</h2><p>{{ summary.narrative }}</p></div></div>
          <div class="metric"><span>Списано по worklog</span><strong>{{ totalHours }}</strong></div>
          <div class="metric"><span>На Code Review</span><strong>{{ reviewHours }}</strong></div>
          <div class="metric"><span>Задач</span><strong>{{ summary.taskCount }}</strong></div>
          <div class="metric"><span>Связанных MR</span><strong>{{ summary.mergeRequestCount }}</strong></div>
        </section>

        <div class="columns">
          <section class="card tasks">
            <div class="section-title"><div><span class="yt">YT</span><h2>Задачи по списаниям</h2></div><small>Выбраны по автору worklog, а не по исполнителю</small></div>
            <article v-for="item in summary.tasks" :key="item.task.id" class="task-card" :class="{ review: isReview(item) }">
              <div class="task-head">
                <div><h3><a :href="item.task.url">{{ item.task.id }}</a> · {{ item.task.summary }}</h3><span v-if="!item.isOwnTask" class="foreign">Чужая задача</span></div>
                <span class="status">{{ item.task.status }}</span>
              </div>
              <div class="worklog-total"><Clock3 :size="18" /><strong>{{ formatDuration(item.loggedMinutes) }}</strong><span>списал выбранный сотрудник</span></div>
              <div class="worklogs">
                <div v-for="worklog in item.worklogs" :key="worklog.id">
                  <span>{{ new Date(worklog.date).toLocaleDateString('ru-RU') }}</span><strong>{{ worklog.type }}</strong><span>{{ formatDuration(worklog.durationMinutes) }}</span><em>{{ worklog.description }}</em>
                </div>
              </div>
              <div v-if="item.task.gitLab" class="gitlab-row">
                <GitMerge :size="17" /><strong>GitLab</strong><span>{{ item.task.gitLab.branch }}</span><span>{{ item.task.gitLab.commitCount }} коммитов</span><span>MR !{{ item.task.gitLab.mergeRequestIid }} · {{ item.task.gitLab.mergeRequestState }}</span>
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
                  <span>{{ taskAnalyses[item.task.id] ? 'Обновить объяснение' : 'Объяснить задачу' }}</span>
                </Button>
                <small>LLM вызывается только по нажатию</small>
              </div>
              <div v-if="taskAnalysisErrors[item.task.id]" class="analysis-error">{{ taskAnalysisErrors[item.task.id] }}</div>
              <section v-if="taskAnalyses[item.task.id]" class="task-analysis">
                <div class="analysis-heading"><Sparkles :size="18" /><strong>Объяснение задачи</strong></div>
                <div class="analysis-context">
                  <span>{{ taskAnalyses[item.task.id].context }}</span>
                  <strong>{{ taskAnalyses[item.task.id].type }}</strong>
                </div>
                <dl>
                  <div><dt>О чём задача</dt><dd>{{ taskAnalyses[item.task.id].taskSummary }}</dd></div>
                  <div><dt>Что сделал сотрудник</dt><dd>{{ taskAnalyses[item.task.id].employeeContribution }}</dd></div>
                  <div><dt>Результат</dt><dd>{{ taskAnalyses[item.task.id].result }}</dd></div>
                </dl>
              </section>
            </article>
          </section>

          <section class="card timeline">
            <div class="section-title"><div><span class="round-icon"><CalendarDays :size="20" /></span><h2>Хронология</h2></div></div>
            <div v-for="event in summary.events" :key="event.id" class="timeline-item">
              <span class="dot" :class="event.source.toLowerCase()"></span>
              <time>{{ new Date(event.occurredAt).toLocaleString('ru-RU', { day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' }) }}</time>
              <div><span class="source" :class="event.source.toLowerCase()">{{ event.source }}</span><strong>{{ event.title }}</strong><p>{{ event.details }}</p></div>
            </div>
            <div class="verified"><CheckCircle2 :size="18" /><span>Worklog и GitLab отображаются раздельно. GitLab-события не пересчитываются в часы.</span></div>
          </section>
        </div>
      </template>
    </main>
  </div>
</template>

