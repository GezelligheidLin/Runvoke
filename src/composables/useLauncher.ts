import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { invoke } from '@tauri-apps/api/core'
import { listen, type UnlistenFn } from '@tauri-apps/api/event'
import type { LogEntry, ProjectConfig, RuntimeStatus } from '../types'

const LOG_LIMIT = 2_000

function stripTerminalControlSequences(message: string) {
  return message
    .replace(/\u001B\][^\u0007]*(?:\u0007|\u001B\\)/g, '')
    .replace(/\u001B\[[0-?]*[ -/]*[@-~]/g, '')
}

function stoppedStatus(projectId: string): RuntimeStatus {
  return {
    projectId,
    state: 'stopped',
    pid: null,
    startedAt: null,
    exitCode: null,
  }
}

export function useLauncher() {
  const projects = ref<ProjectConfig[]>([])
  const runtimeById = ref<Record<string, RuntimeStatus>>({})
  const logsById = ref<Record<string, LogEntry[]>>({})
  const selectedId = ref<string | null>(null)
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

  const selectedRuntime = computed(() => {
    if (!selectedId.value)
      return null
    return runtimeById.value[selectedId.value] ?? stoppedStatus(selectedId.value)
  })

  const selectedLogs = computed(() => {
    if (!selectedId.value)
      return []
    return logsById.value[selectedId.value] ?? []
  })

  function reportError(value: unknown) {
    error.value = value instanceof Error ? value.message : String(value)
  }

  function updateRuntime(status: RuntimeStatus) {
    runtimeById.value = {
      ...runtimeById.value,
      [status.projectId]: status,
    }
  }

  function appendLog(payload: Omit<LogEntry, 'id'>) {
    if (payload.projectId === 'app')
      return
    const previous = logsById.value[payload.projectId] ?? []
    const next = [
      ...previous,
      {
        ...payload,
        message: stripTerminalControlSequences(payload.message),
        id: `${payload.timestamp}-${crypto.randomUUID()}`,
      },
    ].slice(-LOG_LIMIT)
    logsById.value = { ...logsById.value, [payload.projectId]: next }
  }

  async function refreshProjects() {
    projects.value = await invoke<ProjectConfig[]>('list_projects')
    if (!selectedId.value || !projects.value.some((project) => project.id === selectedId.value))
      selectedId.value = projects.value[0]?.id ?? null
  }

  async function refreshRuntime() {
    const statuses = await invoke<RuntimeStatus[]>('list_runtime_status')
    runtimeById.value = Object.fromEntries(statuses.map((status) => [status.projectId, status]))
  }

  async function initialize() {
    loading.value = true
    error.value = null
    try {
      unlisteners.push(
        await listen<LogEntry>('project-log', ({ payload }) => appendLog(payload)),
        await listen<RuntimeStatus>('project-status', ({ payload }) => updateRuntime(payload)),
      )
      await Promise.all([
        refreshProjects(),
        refreshRuntime(),
        invoke<boolean>('get_autostart_enabled').then((enabled) => {
          autostartEnabled.value = enabled
        }),
      ])
      pollTimer = setInterval(() => void refreshRuntime().catch(reportError), 1_500)
      clockTimer = setInterval(() => {
        now.value = Date.now()
      }, 1_000)
    }
    catch (value) {
      reportError(value)
    }
    finally {
      loading.value = false
    }
  }

  async function saveProject(project: ProjectConfig) {
    const saved = await invoke<ProjectConfig>('save_project', { project })
    await refreshProjects()
    selectedId.value = saved.id
    return saved
  }

  async function removeProject(projectId: string) {
    await invoke('delete_project', { projectId })
    delete logsById.value[projectId]
    await Promise.all([refreshProjects(), refreshRuntime()])
  }

  async function runProject(projectId: string) {
    updateRuntime({ ...stoppedStatus(projectId), state: 'starting' })
    try {
      updateRuntime(await invoke<RuntimeStatus>('start_project', { projectId }))
    }
    catch (value) {
      updateRuntime(stoppedStatus(projectId))
      throw value
    }
  }

  async function stopProject(projectId: string) {
    const current = runtimeById.value[projectId] ?? stoppedStatus(projectId)
    updateRuntime({ ...current, state: 'stopping' })
    try {
      updateRuntime(await invoke<RuntimeStatus>('stop_project', { projectId }))
    }
    catch (value) {
      await refreshRuntime()
      throw value
    }
  }

  async function restartProject(projectId: string) {
    const current = runtimeById.value[projectId] ?? stoppedStatus(projectId)
    updateRuntime({ ...current, state: 'starting' })
    try {
      updateRuntime(await invoke<RuntimeStatus>('restart_project', { projectId }))
    }
    catch (value) {
      await refreshRuntime()
      throw value
    }
  }

  async function openInVscode(directory: string) {
    await invoke('open_in_vscode', { directory })
  }

  async function setAutostart(enabled: boolean) {
    autostartEnabled.value = await invoke<boolean>('set_autostart_enabled', { enabled })
  }

  function clearLogs(projectId: string) {
    logsById.value = { ...logsById.value, [projectId]: [] }
  }

  function formatUptime(startedAt: number | null | undefined) {
    if (!startedAt)
      return '—'
    const totalSeconds = Math.max(0, Math.floor((now.value - startedAt) / 1_000))
    const hours = Math.floor(totalSeconds / 3_600)
    const minutes = Math.floor((totalSeconds % 3_600) / 60)
    const seconds = totalSeconds % 60
    return `${hours.toString().padStart(2, '0')}:${minutes.toString().padStart(2, '0')}:${seconds.toString().padStart(2, '0')}`
  }

  onMounted(() => void initialize())
  onBeforeUnmount(() => {
    if (pollTimer)
      clearInterval(pollTimer)
    if (clockTimer)
      clearInterval(clockTimer)
    for (const unlisten of unlisteners)
      unlisten()
  })

  return {
    projects,
    selectedId,
    selectedProject,
    selectedRuntime,
    selectedLogs,
    runtimeById,
    loading,
    error,
    autostartEnabled,
    saveProject,
    removeProject,
    runProject,
    stopProject,
    restartProject,
    openInVscode,
    setAutostart,
    clearLogs,
    formatUptime,
  }
}
