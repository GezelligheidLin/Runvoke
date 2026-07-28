import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { invoke } from '@tauri-apps/api/core'
import { listen, type UnlistenFn } from '@tauri-apps/api/event'
import type { LogEntry, ProjectConfig, ProjectTask, RuntimeStatus } from '../types'

const LOG_LIMIT = 2_000

function stripTerminalControlSequences(message: string) {
  return message
    .replace(/\u001B\][^\u0007]*(?:\u0007|\u001B\\)/g, '')
    .replace(/\u001B\[[0-?]*[ -/]*[@-~]/g, '')
}

export function useLauncher() {
  const projects = ref<ProjectConfig[]>([])
  const runsById = ref<Record<string, RuntimeStatus>>({})
  const logsByRunId = ref<Record<string, LogEntry[]>>({})
  const selectedId = ref<string | null>(null)
  const selectedRunId = ref<string | null>(null)
  const loading = ref(true)
  const error = ref<string | null>(null)
  const autostartEnabled = ref(false)
  const now = ref(Date.now())
  const unlisteners: UnlistenFn[] = []
  let pollTimer: ReturnType<typeof setInterval> | undefined
  let clockTimer: ReturnType<typeof setInterval> | undefined

  const selectedProject = computed(() =>
    projects.value.find((project) => project.id === selectedId.value) ?? null,
  )
  const projectRuns = computed(() => Object.values(runsById.value)
    .filter((run) => run.projectId === selectedId.value)
    .sort((left, right) => (right.startedAt ?? 0) - (left.startedAt ?? 0)))
  const selectedRun = computed(() =>
    projectRuns.value.find((run) => run.runId === selectedRunId.value) ?? projectRuns.value[0] ?? null,
  )
  const selectedLogs = computed(() =>
    selectedRun.value ? logsByRunId.value[selectedRun.value.runId] ?? [] : [],
  )

  function reportError(value: unknown) {
    error.value = value instanceof Error ? value.message : String(value)
  }

  function updateRun(status: RuntimeStatus) {
    runsById.value = { ...runsById.value, [status.runId]: status }
  }

  function appendLog(payload: Omit<LogEntry, 'id'>) {
    if (payload.projectId === 'app')
      return
    const previous = logsByRunId.value[payload.runId] ?? []
    logsByRunId.value = {
      ...logsByRunId.value,
      [payload.runId]: [...previous, {
        ...payload,
        message: stripTerminalControlSequences(payload.message),
        id: `${payload.timestamp}-${crypto.randomUUID()}`,
      }].slice(-LOG_LIMIT),
    }
  }

  async function refreshProjects() {
    projects.value = await invoke<ProjectConfig[]>('list_projects')
    if (!selectedId.value || !projects.value.some((project) => project.id === selectedId.value))
      selectedId.value = projects.value[0]?.id ?? null
  }

  async function refreshRuntime() {
    const statuses = await invoke<RuntimeStatus[]>('list_runtime_status')
    runsById.value = Object.fromEntries(statuses.map((status) => [status.runId, status]))
  }

  async function initialize() {
    loading.value = true
    error.value = null
    try {
      unlisteners.push(
        await listen<LogEntry>('project-log', ({ payload }) => appendLog(payload)),
        await listen<RuntimeStatus>('project-status', ({ payload }) => updateRun(payload)),
      )
      await Promise.all([
        refreshProjects(),
        refreshRuntime(),
        invoke<boolean>('get_autostart_enabled').then((enabled) => { autostartEnabled.value = enabled }),
      ])
      pollTimer = setInterval(() => void refreshRuntime().catch(reportError), 1_500)
      clockTimer = setInterval(() => { now.value = Date.now() }, 1_000)
    }
    catch (value) { reportError(value) }
    finally { loading.value = false }
  }

  async function saveProject(project: ProjectConfig) {
    const saved = await invoke<ProjectConfig>('save_project', { project })
    await refreshProjects()
    selectedId.value = saved.id
    return saved
  }

  async function removeProject(projectId: string) {
    await invoke('delete_project', { projectId })
    Object.values(runsById.value).filter((run) => run.projectId === projectId).forEach((run) => delete logsByRunId.value[run.runId])
    await Promise.all([refreshProjects(), refreshRuntime()])
  }

  async function runTask(projectId: string, task: ProjectTask) {
    const status = await invoke<RuntimeStatus>('run_task', { projectId, taskId: task.id })
    updateRun(status)
    selectedRunId.value = status.runId
    return status
  }

  async function runTemporaryCommand(projectId: string, command: string) {
    const status = await invoke<RuntimeStatus>('run_temporary_command', { projectId, command })
    updateRun(status)
    selectedRunId.value = status.runId
    return status
  }

  async function stopRun(runId: string) {
    updateRun(await invoke<RuntimeStatus>('stop_run', { runId }))
  }

  async function dismissRun(runId: string) {
    await invoke('dismiss_run', { runId })
    const { [runId]: removedRun, ...remainingRuns } = runsById.value
    const { [runId]: removedLogs, ...remainingLogs } = logsByRunId.value
    runsById.value = remainingRuns
    logsByRunId.value = remainingLogs
    if (selectedRunId.value === runId)
      selectedRunId.value = null
    void removedRun
    void removedLogs
  }

  async function dismissInactiveRuns() {
    const runIds = await invoke<string[]>('dismiss_inactive_runs')
    if (!runIds.length)
      return 0
    const removedRunIds = new Set(runIds)
    runsById.value = Object.fromEntries(Object.entries(runsById.value).filter(([runId]) => !removedRunIds.has(runId)))
    logsByRunId.value = Object.fromEntries(Object.entries(logsByRunId.value).filter(([runId]) => !removedRunIds.has(runId)))
    if (selectedRunId.value && removedRunIds.has(selectedRunId.value))
      selectedRunId.value = null
    return runIds.length
  }

  async function openInVscode(directory: string) { await invoke('open_in_vscode', { directory }) }
  async function openInFileManager(directory: string) { await invoke('open_in_file_manager', { directory }) }
  async function setAutostart(enabled: boolean) { autostartEnabled.value = await invoke<boolean>('set_autostart_enabled', { enabled }) }
  function clearLogs(runId: string) { logsByRunId.value = { ...logsByRunId.value, [runId]: [] } }
  function formatUptime(startedAt: number | null | undefined) {
    if (!startedAt) return '—'
    const totalSeconds = Math.max(0, Math.floor((now.value - startedAt) / 1_000))
    return [Math.floor(totalSeconds / 3_600), Math.floor((totalSeconds % 3_600) / 60), totalSeconds % 60]
      .map((value) => value.toString().padStart(2, '0')).join(':')
  }

  onMounted(() => void initialize())
  onBeforeUnmount(() => {
    if (pollTimer) clearInterval(pollTimer)
    if (clockTimer) clearInterval(clockTimer)
    unlisteners.forEach((unlisten) => unlisten())
  })

  return {
    projects, selectedId, selectedProject, selectedRunId, selectedRun, selectedLogs, projectRuns, runsById,
    loading, error, autostartEnabled, saveProject, removeProject, runTask, runTemporaryCommand, stopRun, dismissRun, dismissInactiveRuns,
    openInVscode, openInFileManager, setAutostart, clearLogs, formatUptime,
  }
}
