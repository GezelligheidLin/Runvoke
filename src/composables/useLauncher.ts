import { computed, onBeforeUnmount, onMounted, ref, shallowRef } from 'vue'
import { invoke } from '@tauri-apps/api/core'
import { listen, type UnlistenFn } from '@tauri-apps/api/event'
import type { LogEntry, ProjectConfig, ProjectGroup, ProjectTask, RuntimeStatus } from '../types'

const LOG_LIMIT = 2_000
const LOG_FLUSH_INTERVAL = 50
const LOG_MESSAGE_LIMIT = 16_000

type LogMode = 'append' | 'progress' | 'finish'
type IncomingLog = Omit<LogEntry, 'id' | 'transient'> & { mode?: LogMode }

interface LauncherOptions {
  onRuntimeStatusChanged?: (status: RuntimeStatus, previous: RuntimeStatus) => void
}

function stripTerminalControlSequences(message: string) {
  const stripped = message
    .replace(/\u001B\][^\u0007]*(?:\u0007|\u001B\\)/g, '')
    .replace(/\u001B\[[0-?]*[ -/]*[@-~]/g, '')
  return stripped.length > LOG_MESSAGE_LIMIT
    ? `${stripped.slice(0, LOG_MESSAGE_LIMIT)}… [单条日志过长，已截断]`
    : stripped
}

function isReplaceableProgress(message: string) {
  return /\[webpack\.Progress\]\s+\d{1,3}%(?:\s|$)/i.test(message)
}

export function useLauncher(options: LauncherOptions = {}) {
  const projects = ref<ProjectConfig[]>([])
  const projectGroups = ref<ProjectGroup[]>([])
  const runsById = ref<Record<string, RuntimeStatus>>({})
  const logsByRunId = shallowRef<Record<string, LogEntry[]>>({})
  const selectedId = ref<string | null>(null)
  const selectedRunId = ref<string | null>(null)
  const loading = ref(true)
  const error = ref<string | null>(null)
  const autostartEnabled = ref(false)
  const now = ref(Date.now())
  const unlisteners: UnlistenFn[] = []
  let pollTimer: ReturnType<typeof setInterval> | undefined
  let clockTimer: ReturnType<typeof setInterval> | undefined
  let logFlushTimer: ReturnType<typeof setTimeout> | undefined
  let logSequence = 0
  const pendingLogsByRunId = new Map<string, IncomingLog[]>()

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

  function updateRun(status: RuntimeStatus, announce = true) {
    const previous = runsById.value[status.runId]
    runsById.value = { ...runsById.value, [status.runId]: status }
    if (!announce)
      return
    if (previous && previous.state !== status.state)
      options.onRuntimeStatusChanged?.(status, previous)
  }

  function appendLogs(payloads: IncomingLog[]) {
    for (const payload of payloads) {
      if (payload.projectId === 'app')
        continue
      const pending = pendingLogsByRunId.get(payload.runId) ?? []
      pending.push(payload)
      if (pending.length > LOG_LIMIT)
        pending.splice(0, pending.length - LOG_LIMIT)
      pendingLogsByRunId.set(payload.runId, pending)
    }
    if (pendingLogsByRunId.size)
      logFlushTimer ??= setTimeout(flushPendingLogs, LOG_FLUSH_INTERVAL)
  }

  function flushPendingLogs() {
    logFlushTimer = undefined
    if (!pendingLogsByRunId.size)
      return

    const nextLogs = { ...logsByRunId.value }
    for (const [runId, pending] of pendingLogsByRunId) {
      const entries = [...(nextLogs[runId] ?? [])]
      for (const incoming of pending) {
        const { mode = 'append', ...payload } = incoming
        const message = stripTerminalControlSequences(payload.message)
        const effectiveMode: LogMode = mode === 'append' && isReplaceableProgress(message)
          ? 'progress'
          : mode
        const entry: LogEntry = {
          ...payload,
          message,
          id: `${payload.timestamp}-${++logSequence}`,
          transient: effectiveMode === 'progress',
        }
        let replaceIndex = -1
        if (effectiveMode !== 'append') {
          for (let index = entries.length - 1; index >= 0; index--) {
            if (entries[index]?.transient && entries[index]?.stream === entry.stream) {
              replaceIndex = index
              break
            }
          }
        }
        if (replaceIndex >= 0)
          entries[replaceIndex] = entry
        else {
          if (effectiveMode === 'append') {
            for (let index = entries.length - 1; index >= 0; index--) {
              const existing = entries[index]
              if (existing?.transient && existing.stream === entry.stream) {
                entries[index] = { ...existing, transient: false }
                break
              }
            }
          }
          entries.push(entry)
        }
      }
      nextLogs[runId] = entries.slice(-LOG_LIMIT)
    }
    pendingLogsByRunId.clear()
    logsByRunId.value = nextLogs
  }

  async function refreshProjects() {
    projects.value = await invoke<ProjectConfig[]>('list_projects')
    if (!selectedId.value || !projects.value.some((project) => project.id === selectedId.value))
      selectedId.value = projects.value[0]?.id ?? null
  }

  async function refreshProjectGroups() {
    projectGroups.value = await invoke<ProjectGroup[]>('list_project_groups')
  }

  async function refreshWorkspace() {
    await Promise.all([refreshProjects(), refreshProjectGroups()])
  }

  async function refreshRuntime(announce = false) {
    const statuses = await invoke<RuntimeStatus[]>('list_runtime_status')
    const previousRuns = runsById.value
    runsById.value = Object.fromEntries(statuses.map((status) => [status.runId, status]))
    if (!announce)
      return
    for (const status of statuses) {
      const previous = previousRuns[status.runId]
      if (previous && previous.state !== status.state)
        options.onRuntimeStatusChanged?.(status, previous)
    }
  }

  async function initialize() {
    loading.value = true
    error.value = null
    try {
      unlisteners.push(
        await listen<IncomingLog[]>('project-logs', ({ payload }) => appendLogs(payload)),
        await listen<RuntimeStatus>('project-status', ({ payload }) => updateRun(payload)),
      )
      await Promise.all([
        refreshWorkspace(),
        refreshRuntime(),
        invoke<boolean>('get_autostart_enabled').then((enabled) => { autostartEnabled.value = enabled }),
      ])
      pollTimer = setInterval(() => void refreshRuntime(true).catch(reportError), 1_500)
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

  async function reorderProjects(projectIds: string[]) {
    const previous = projects.value
    const projectsById = new Map(previous.map(project => [project.id, project]))
    const reordered = projectIds.map(projectId => projectsById.get(projectId))
    if (reordered.some(project => !project) || reordered.length !== previous.length)
      throw new Error('项目排序数据无效')

    projects.value = reordered as ProjectConfig[]
    try {
      await invoke('reorder_projects', { projectIds })
    }
    catch (value) {
      projects.value = previous
      throw value
    }
  }

  async function saveProjectGroup(group: ProjectGroup) {
    const saved = await invoke<ProjectGroup>('save_project_group', { group })
    await refreshProjectGroups()
    return saved
  }

  async function removeProjectGroup(groupId: string) {
    await invoke('delete_project_group', { groupId })
    await Promise.all([refreshProjectGroups(), refreshProjects()])
  }

  async function setProjectGroupCollapsed(groupId: string, collapsed: boolean) {
    const previous = projectGroups.value
    projectGroups.value = previous.map(group => group.id === groupId ? { ...group, collapsed } : group)
    try {
      await invoke<ProjectGroup>('set_project_group_collapsed', { groupId, collapsed })
    }
    catch (value) {
      projectGroups.value = previous
      throw value
    }
  }

  async function setProjectGroupsCollapsed(collapsed: boolean) {
    const previous = projectGroups.value
    projectGroups.value = previous.map(group => ({ ...group, collapsed }))
    try {
      projectGroups.value = await invoke<ProjectGroup[]>('set_project_groups_collapsed', { collapsed })
    }
    catch (value) {
      projectGroups.value = previous
      throw value
    }
  }

  async function moveProject(projectId: string, groupId: string | null, targetIndex: number) {
    const previous = projects.value
    const sourceIndex = previous.findIndex(project => project.id === projectId)
    if (sourceIndex < 0)
      throw new Error('项目不存在')
    const next = [...previous]
    const [source] = next.splice(sourceIndex, 1)
    if (!source)
      throw new Error('项目不存在')
    const moved = { ...source, groupId }
    const matchingIndices = next
      .map((project, index) => project.groupId === groupId ? index : -1)
      .filter(index => index >= 0)
    const insertIndex = matchingIndices[targetIndex] ?? (matchingIndices.length ? matchingIndices.at(-1)! + 1 : next.length)
    next.splice(insertIndex, 0, moved)
    projects.value = next
    try {
      projects.value = await invoke<ProjectConfig[]>('move_project', { projectId, groupId, targetIndex })
    }
    catch (value) {
      projects.value = previous
      throw value
    }
  }

  async function removeProject(projectId: string) {
    await invoke('delete_project', { projectId })
    const removedRunIds = new Set(Object.values(runsById.value).filter(run => run.projectId === projectId).map(run => run.runId))
    removedRunIds.forEach(runId => pendingLogsByRunId.delete(runId))
    logsByRunId.value = Object.fromEntries(Object.entries(logsByRunId.value).filter(([runId]) => !removedRunIds.has(runId)))
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
    const current = runsById.value[runId]
    if (current)
      updateRun({ ...current, state: 'stopping' })
    try {
      updateRun(await invoke<RuntimeStatus>('stop_run', { runId }))
    }
    catch (value) {
      await refreshRuntime().catch(reportError)
      throw value
    }
  }

  async function dismissRun(runId: string) {
    await invoke('dismiss_run', { runId })
    pendingLogsByRunId.delete(runId)
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
    removedRunIds.forEach(runId => pendingLogsByRunId.delete(runId))
    runsById.value = Object.fromEntries(Object.entries(runsById.value).filter(([runId]) => !removedRunIds.has(runId)))
    logsByRunId.value = Object.fromEntries(Object.entries(logsByRunId.value).filter(([runId]) => !removedRunIds.has(runId)))
    if (selectedRunId.value && removedRunIds.has(selectedRunId.value))
      selectedRunId.value = null
    return runIds.length
  }

  async function openInVscode(directory: string) { await invoke('open_in_vscode', { directory }) }
  async function openInFileManager(directory: string) { await invoke('open_in_file_manager', { directory }) }
  async function setAutostart(enabled: boolean) { autostartEnabled.value = await invoke<boolean>('set_autostart_enabled', { enabled }) }
  function clearLogs(runId: string) {
    pendingLogsByRunId.delete(runId)
    logsByRunId.value = { ...logsByRunId.value, [runId]: [] }
  }
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
    if (logFlushTimer) clearTimeout(logFlushTimer)
    pendingLogsByRunId.clear()
    unlisteners.forEach((unlisten) => unlisten())
  })

  return {
    projects, projectGroups, selectedId, selectedProject, selectedRunId, selectedRun, selectedLogs, projectRuns, runsById,
    loading, error, autostartEnabled, saveProject, reorderProjects, saveProjectGroup, removeProjectGroup, setProjectGroupCollapsed, setProjectGroupsCollapsed, moveProject, removeProject, runTask, runTemporaryCommand, stopRun, dismissRun, dismissInactiveRuns,
    openInVscode, openInFileManager, setAutostart, clearLogs, formatUptime, refreshWorkspace,
  }
}
