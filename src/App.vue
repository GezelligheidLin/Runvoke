<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, useTemplateRef, watch } from 'vue'
import { getVersion } from '@tauri-apps/api/app'
import { invoke, isTauri } from '@tauri-apps/api/core'
import { listen, type UnlistenFn } from '@tauri-apps/api/event'
import { getCurrentWindow } from '@tauri-apps/api/window'
import { relaunch } from '@tauri-apps/plugin-process'
import { check, type DownloadEvent, type Update } from '@tauri-apps/plugin-updater'
import brandIcon from '../src-tauri/icons/128x128.png'
import OverflowTooltip from './components/OverflowTooltip.vue'
import ProjectForm from './components/ProjectForm.vue'
import ProjectGroupList from './components/ProjectGroupList.vue'
import ImportPromptDialog from './components/ImportPromptDialog.vue'
import AgentProjectImportDialog from './components/AgentProjectImportDialog.vue'
import ProjectImportDialog from './components/ProjectImportDialog.vue'
import ResizableSplitPane from './components/ResizableSplitPane.vue'
import SettingsPage from './components/SettingsPage.vue'
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from './components/ui/tooltip'
import { useLauncher } from './composables/useLauncher'
import type { ImportedProject, LogStream, McpServerStatus, NotificationPosition, ProjectConfig, ProjectGroup, ProjectImportSource, ProjectTask, RuntimeStatus } from './types'

const {
  projects,
  projectGroups,
  selectedId,
  selectedProject,
  selectedRunId,
  selectedRun,
  selectedLogs,
  projectRuns,
  runsById,
  loading,
  error,
  autostartEnabled,
  saveProject,
  saveProjectGroup,
  removeProjectGroup,
  setProjectGroupCollapsed,
  moveProject,
  removeProject,
  runTask,
  runTemporaryCommand,
  stopRun,
  dismissRun,
  dismissInactiveRuns,
  openInVscode,
  openInFileManager,
  setAutostart,
  clearLogs,
  refreshWorkspace,
} = useLauncher({
  onRuntimeStatusChanged: reportRuntimeStatusChange,
})

const search = ref('')
const formOpen = ref(false)
const editingProject = ref<ProjectConfig | null>(null)
const settingsOpen = ref(false)
type Theme = 'light' | 'dark'
type LogLinkAction = 'open' | 'copy'
type PreviewUpdate = {
  version: string
  body: string
}
type PreviewUpdateDownloadProgress = {
  received: number
  total?: number
}
type AvailableUpdate =
  | { channel: 'stable', update: Update, version: string, body: string }
  | { channel: 'preview', version: string, body: string }
const theme = ref<Theme>(readTheme())
const logLinkAction = ref<LogLinkAction>(readLogLinkAction())
const githubLinkVisible = ref(readGithubLinkVisible())
const previewUpdatesEnabled = ref(readPreviewUpdatesEnabled())
const notificationPosition = ref<NotificationPosition>(readNotificationPosition())
const notificationStackingEnabled = ref(readNotificationStackingEnabled())
const saving = ref(false)
const busyAction = ref('')
const temporaryCommand = ref('')
const projectOpenMenuOpen = ref(false)
const groupEditorOpen = ref(false)
const groupDraftId = ref('')
const groupDraftName = ref('')
const groupDraftProjectId = ref('')
const groupSaving = ref(false)
const activeGroupMenuId = ref<string | null>(null)
const ungroupedCollapsed = ref(readUngroupedCollapsed())
const logFilter = ref<'all' | LogStream>('all')
const autoScroll = ref(true)
const toast = ref<{ type: 'success' | 'error', message: string } | null>(null)
const appVersion = shallowRef('')
const availableUpdate = shallowRef<AvailableUpdate | null>(null)
const updatePopoverOpen = ref(false)
const updateChecking = ref(false)
const updateInstalling = ref(false)
const notificationTesting = ref(false)
const projectConfigOpening = ref(false)
const projectImportSource = ref<ProjectImportSource>('vscode')
const importPromptOpen = ref(false)
const projectImportDialogOpen = ref(false)
const projectImportCandidates = ref<ImportedProject[]>([])
const projectImportLoading = ref(false)
const projectImporting = ref(false)
const agentProjectImportDialogOpen = ref(false)
const agentProjectImportCandidates = ref<ImportedProject[]>([])
const agentProjectImporting = ref(false)
const mcpStatus = ref<McpServerStatus | null>(null)
const mcpBusy = ref(false)
const updateProgress = ref({ received: 0, total: 0 })
const logContainer = useTemplateRef<HTMLDivElement>('logContainer')
const runListContainer = useTemplateRef<HTMLDivElement>('runListContainer')
const projectListContainer = useTemplateRef<HTMLElement>('projectListContainer')
const groupNameInput = useTemplateRef<HTMLInputElement>('groupNameInput')
const confirmationCancelButton = useTemplateRef<HTMLButtonElement>('confirmationCancelButton')
const contextMenuEditButton = useTemplateRef<HTMLButtonElement>('contextMenuEditButton')

type ScrollbarState = {
  overflow: boolean
  hovered: boolean
  thumbHeight: number
  thumbTop: number
}

const runListScrollbar = ref<ScrollbarState>({ overflow: false, hovered: false, thumbHeight: 0, thumbTop: 0 })
const logScrollbar = ref<ScrollbarState>({ overflow: false, hovered: false, thumbHeight: 0, thumbTop: 0 })
const projectListScrollbar = ref<ScrollbarState>({ overflow: false, hovered: false, thumbHeight: 0, thumbTop: 0 })
let draggingScrollbar: {
  container: HTMLElement
  scrollbar: ScrollbarState
  startY: number
  startScrollTop: number
} | null = null

type PendingConfirmation = {
  message: string
  confirmLabel: string
  variant: 'danger' | 'primary'
  top: number
  left: number
  width: number
  action: () => Promise<void>
}

type ProjectContextMenu = {
  project: ProjectConfig
  top: number
  left: number
  submenuTop: number
  submenuLeft: number
}

const pendingConfirmation = ref<PendingConfirmation | null>(null)
const projectContextMenu = ref<ProjectContextMenu | null>(null)
const projectContextSubmenuOpen = ref(false)
let toastTimer: number | undefined
let updateCheckTimer: number | undefined
let mcpUnlisteners: UnlistenFn[] = []
let mcpWorkspaceRefreshTimer: number | undefined
let projectContextSubmenuCloseTimer: number | undefined
let scrollbarUpdateFrame: number | undefined
const updateCheckInterval = 30 * 60 * 1_000
const intentionallyStoppedRunIds = new Set<string>()
const notifiedRunIds = new Set<string>()

function readTheme(): Theme {
  try {
    return window.localStorage.getItem('runvoke-theme') === 'dark' ? 'dark' : 'light'
  }
  catch {
    return 'light'
  }
}

function readUngroupedCollapsed() {
  try {
    return window.localStorage.getItem('runvoke-ungrouped-collapsed') === 'true'
  }
  catch {
    return false
  }
}

function readLogLinkAction(): LogLinkAction {
  try {
    return window.localStorage.getItem('runvoke-log-link-action') === 'copy' ? 'copy' : 'open'
  }
  catch {
    return 'open'
  }
}

function readGithubLinkVisible() {
  try {
    return window.localStorage.getItem('runvoke-github-link-visible') !== 'false'
  }
  catch {
    return true
  }
}

function readPreviewUpdatesEnabled() {
  try {
    return window.localStorage.getItem('runvoke-preview-updates-enabled') === 'true'
  }
  catch {
    return false
  }
}

function readNotificationPosition(): NotificationPosition {
  const positions: NotificationPosition[] = [
    'top-left',
    'top-center',
    'top-right',
    'bottom-left',
    'bottom-center',
    'bottom-right',
  ]
  try {
    const value = window.localStorage.getItem('runvoke-notification-position') as NotificationPosition | null
    return value && positions.includes(value) ? value : 'bottom-right'
  }
  catch {
    return 'bottom-right'
  }
}

function readNotificationStackingEnabled() {
  try {
    return window.localStorage.getItem('runvoke-notification-stacking-enabled') === 'true'
  }
  catch {
    return false
  }
}

watch(theme, (value) => {
  const dark = value === 'dark'
  document.documentElement.dataset.theme = value
  document.body.classList.toggle('theme-dark', dark)
  if (isTauri()) {
    const backgroundColor: [number, number, number] = dark ? [23, 26, 24] : [247, 245, 241]
    const [red, green, blue] = backgroundColor
    void Promise.all([
      getCurrentWindow().setBackgroundColor(backgroundColor),
      invoke('set_resize_paint_color', { red, green, blue }),
    ]).catch(() => {
      // CSS still provides the correct fallback background if the native call fails.
    })
  }
  try {
    window.localStorage.setItem('runvoke-theme', value)
  }
  catch {
    // Theme persistence is optional when storage is unavailable.
  }
}, { immediate: true })

watch(ungroupedCollapsed, (value) => {
  try {
    window.localStorage.setItem('runvoke-ungrouped-collapsed', String(value))
  }
  catch {
    // Local persistence is optional when storage is unavailable.
  }
})

watch(logLinkAction, (value) => {
  try {
    window.localStorage.setItem('runvoke-log-link-action', value)
  }
  catch {
    // Link behavior persistence is optional when storage is unavailable.
  }
})

watch(githubLinkVisible, (value) => {
  try {
    window.localStorage.setItem('runvoke-github-link-visible', String(value))
  }
  catch {
    // Repository entry visibility is optional when storage is unavailable.
  }
})

watch(previewUpdatesEnabled, (enabled) => {
  try {
    window.localStorage.setItem('runvoke-preview-updates-enabled', String(enabled))
  }
  catch {
    // Preview update preference persistence is optional when storage is unavailable.
  }
  if (!enabled && availableUpdate.value?.channel === 'preview')
    availableUpdate.value = null
  void checkForUpdate()
})

watch(notificationPosition, (position) => {
  try {
    window.localStorage.setItem('runvoke-notification-position', position)
  }
  catch {
    // Notification position persistence is optional when storage is unavailable.
  }
})

watch(notificationStackingEnabled, (enabled) => {
  try {
    window.localStorage.setItem('runvoke-notification-stacking-enabled', String(enabled))
  }
  catch {
    // Notification stacking persistence is optional when storage is unavailable.
  }
})

type LogSegment = {
  text: string
  url?: string
}

function splitLogMessage(message: string): LogSegment[] {
  const segments: LogSegment[] = []
  const pattern = /https?:\/\/[^\s<>"']+/gi
  let cursor = 0
  for (const match of message.matchAll(pattern)) {
    const start = match.index ?? 0
    const candidate = match[0]
    const url = candidate.replace(/[),.;!?]+$/g, '')
    if (start > cursor)
      segments.push({ text: message.slice(cursor, start) })
    if (url)
      segments.push({ text: url, url })
    if (candidate.length > url.length)
      segments.push({ text: candidate.slice(url.length) })
    cursor = start + candidate.length
  }
  if (cursor < message.length)
    segments.push({ text: message.slice(cursor) })
  return segments.length ? segments : [{ text: message || ' ' }]
}

async function handleLogLink(url: string) {
  if (logLinkAction.value === 'copy') {
    try {
      if (navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(url)
      }
      else {
        copyTextFallback(url)
      }
      notify('success', '链接已复制')
    }
    catch {
      try {
        copyTextFallback(url)
        notify('success', '链接已复制')
      }
      catch (value) {
        notify('error', `复制链接失败：${String(value)}`)
      }
    }
    return
  }
  try {
    await invoke('open_external_url', { url })
  }
  catch (value) {
    notify('error', `打开链接失败：${String(value)}`)
  }
}

async function openRepository() {
  try {
    await invoke('open_external_url', { url: 'https://github.com/GezelligheidLin/Runvoke' })
  }
  catch (value) {
    notify('error', `打开 GitHub 仓库失败：${String(value)}`)
  }
}

function copyTextFallback(text: string) {
  const textarea = document.createElement('textarea')
  textarea.value = text
  textarea.style.position = 'fixed'
  textarea.style.opacity = '0'
  document.body.appendChild(textarea)
  textarea.select()
  const copied = document.execCommand('copy')
  textarea.remove()
  if (!copied)
    throw new Error('系统拒绝了剪贴板操作')
}

const filteredProjects = computed(() => {
  const keyword = search.value.trim().toLocaleLowerCase()
  if (!keyword)
    return projects.value
  return projects.value.filter((project) =>
    `${project.name} ${project.directory} ${project.command}`.toLocaleLowerCase().includes(keyword),
  )
})
type ProjectGroupSection = {
  id: string | null
  name: string
  collapsed: boolean
  projects: ProjectConfig[]
}

const projectGroupSections = computed<ProjectGroupSection[]>(() => {
  const searching = Boolean(search.value.trim())
  const sections: ProjectGroupSection[] = projectGroups.value.map(group => ({
    id: group.id,
    name: group.name,
    collapsed: searching ? false : group.collapsed,
    projects: filteredProjects.value.filter(project => project.groupId === group.id),
  }))
  const ungroupedProjects = filteredProjects.value.filter(project => !project.groupId)
  if (ungroupedProjects.length || (!searching && projectGroups.value.length)) {
    sections.push({
      id: null,
      name: '未分组',
      collapsed: searching ? false : ungroupedCollapsed.value,
      projects: ungroupedProjects,
    })
  }
  return searching ? sections.filter(section => section.projects.length) : sections
})

const visibleLogs = computed(() => {
  if (logFilter.value === 'all')
    return selectedLogs.value
  return selectedLogs.value.filter((entry) => entry.stream === logFilter.value)
})

const activeRuns = computed(() => Object.values(runsById.value).filter(run => isRunActive(run.state)))
const runningCount = computed(() => activeRuns.value.length)
const operationsLocked = computed(() => updateInstalling.value || busyAction.value === 'stop-all')
const visibleTasks = computed(() => selectedProject.value?.tasks.slice(0, 3) ?? [])
const inactiveRunCount = computed(() => projectRuns.value.filter((run) => !isRunActive(run.state)).length)

watch(
  () => visibleLogs.value.length,
  async () => {
    await nextTick()
    if (autoScroll.value && logContainer.value)
      logContainer.value.scrollTop = logContainer.value.scrollHeight
    updateLogScrollbar()
  },
)

watch(
  () => projectRuns.value.length,
  () => {
    void nextTick(updateRunListScrollbar)
  },
)

watch(
  () => projectGroupSections.value.map(section => `${section.id}:${section.collapsed}:${section.projects.length}`).join('|'),
  () => {
    void nextTick(updateProjectListScrollbar)
  },
)

watch(
  () => [groupEditorOpen.value, activeGroupMenuId.value] as const,
  () => {
    void nextTick(updateProjectListScrollbar)
  },
)

watch(settingsOpen, (open) => {
  if (!open)
    void nextTick(updateScrollbars)
})

onMounted(() => {
  window.addEventListener('resize', scheduleScrollbarUpdate)
  window.addEventListener('contextmenu', preventNativeContextMenu)
  void nextTick(updateScrollbars)
  if (isTauri()) {
    void getVersion().then((version) => {
      appVersion.value = version
      promptProjectImportAfterUpdate(version)
    }).catch(() => {})
  }
  void setupMcpBridge()
  void checkForUpdate()
  updateCheckTimer = window.setInterval(() => void checkForUpdate(), updateCheckInterval)
})

onBeforeUnmount(() => {
  window.removeEventListener('resize', scheduleScrollbarUpdate)
  window.removeEventListener('contextmenu', preventNativeContextMenu)
  if (scrollbarUpdateFrame !== undefined)
    cancelAnimationFrame(scrollbarUpdateFrame)
  if (updateCheckTimer)
    clearInterval(updateCheckTimer)
  if (projectContextSubmenuCloseTimer)
    clearTimeout(projectContextSubmenuCloseTimer)
  if (mcpWorkspaceRefreshTimer)
    clearTimeout(mcpWorkspaceRefreshTimer)
  for (const unlisten of mcpUnlisteners)
    unlisten()
  mcpUnlisteners = []
})

async function isAppInBackground() {
  if (!isTauri())
    return document.visibilityState !== 'visible'
  try {
    const window = getCurrentWindow()
    const [focused, minimized] = await Promise.all([window.isFocused(), window.isMinimized()])
    return !focused || minimized
  }
  catch {
    return document.visibilityState !== 'visible'
  }
}

async function showRuntimeDesktopNotification(
  tone: 'success' | 'error',
  title: string,
  message: string,
  meta: string,
  runId: string,
) {
  if (!isTauri() || !(await isAppInBackground()))
    return
  await invoke('show_desktop_notification', {
    position: 'bottom-right',
    theme: theme.value,
    stackingEnabled: notificationStackingEnabled.value,
    tone,
    title,
    message,
    meta,
    dedupeKey: runId,
  }).catch(() => {})
}

function projectNameForRun(run: RuntimeStatus) {
  return projects.value.find(project => project.id === run.projectId)?.name ?? '项目'
}

function markRunNotification(runId: string) {
  if (notifiedRunIds.has(runId))
    return false
  notifiedRunIds.add(runId)
  if (notifiedRunIds.size > 1_000) {
    const oldestRunId = notifiedRunIds.values().next().value
    if (oldestRunId)
      notifiedRunIds.delete(oldestRunId)
  }
  return true
}

function reportRuntimeStatusChange(run: RuntimeStatus, previous: RuntimeStatus) {
  if (run.state === 'stopped' && intentionallyStoppedRunIds.delete(run.runId))
    return
  const hasEnded = run.state === 'succeeded'
    || run.state === 'failed'
    || (run.state === 'stopped' && previous.state !== 'stopping')
  if (!hasEnded || !markRunNotification(run.runId))
    return
  if (run.state === 'succeeded') {
    void showRuntimeDesktopNotification(
      'success',
      `「${run.taskName}」任务已结束`,
      `项目「${projectNameForRun(run)}」的任务已结束。`,
      `退出码 ${run.exitCode ?? 0}`,
      run.runId,
    )
  }
  else if (run.state === 'failed') {
    void showRuntimeDesktopNotification(
      'error',
      `「${run.taskName}」任务已结束`,
      `项目「${projectNameForRun(run)}」的任务已结束。`,
      `退出码 ${run.exitCode ?? '未知'}`,
      run.runId,
    )
  }
  else if (run.state === 'stopped' && previous.state !== 'stopping') {
    void showRuntimeDesktopNotification(
      'success',
      `「${run.taskName}」任务已结束`,
      `项目「${projectNameForRun(run)}」的任务已结束。`,
      `退出码 ${run.exitCode ?? 0}`,
      run.runId,
    )
  }
}

async function stopRunWithoutDesktopNotification(runId: string) {
  intentionallyStoppedRunIds.add(runId)
  try {
    await stopRun(runId)
  }
  catch (value) {
    intentionallyStoppedRunIds.delete(runId)
    throw value
  }
}

async function setupMcpBridge() {
  if (!isTauri())
    return

  try {
    mcpUnlisteners = await Promise.all([
      listen<McpServerStatus>('mcp-server-status', ({ payload }) => {
        mcpStatus.value = payload
      }),
      listen('mcp-workspace-changed', () => {
        scheduleMcpWorkspaceRefresh()
      }),
      listen<{ projects?: unknown }>('mcp-project-import-request', ({ payload }) => {
        if (!Array.isArray(payload.projects) || !payload.projects.every(isAgentImportCandidate)) {
          notify('error', 'MCP 导入请求未提供有效的候选项目')
          return
        }
        openAgentProjectImportDialog(payload.projects)
      }),
      listen<Record<string, unknown>>('mcp-settings-update', ({ payload }) => {
        applyMcpSettings(payload)
      }),
      listen('mcp-check-updates', () => {
        void checkForUpdate(true)
      }),
      listen('mcp-install-update', () => {
        requestInstallAvailableUpdateFromMcp()
      }),
      listen<PreviewUpdateDownloadProgress>('preview-update-download-progress', ({ payload }) => {
        updateProgress.value = {
          received: payload.received,
          total: payload.total ?? 0,
        }
      }),
    ])
    mcpStatus.value = await invoke<McpServerStatus>('get_mcp_server_status')
  }
  catch (value) {
    notify('error', `MCP 服务状态读取失败：${String(value)}`)
  }
}

function scheduleMcpWorkspaceRefresh() {
  if (mcpWorkspaceRefreshTimer)
    clearTimeout(mcpWorkspaceRefreshTimer)
  mcpWorkspaceRefreshTimer = window.setTimeout(() => {
    mcpWorkspaceRefreshTimer = undefined
    void refreshWorkspace().catch((value) => {
      notify('error', `同步 MCP 工作区失败：${String(value)}`)
    })
  }, 80)
}

function applyMcpSettings(payload: Record<string, unknown>) {
  if (payload.theme === 'light' || payload.theme === 'dark')
    theme.value = payload.theme
  if (payload.logLinkAction === 'open' || payload.logLinkAction === 'copy')
    logLinkAction.value = payload.logLinkAction
  if (typeof payload.githubLinkVisible === 'boolean')
    githubLinkVisible.value = payload.githubLinkVisible
  if (typeof payload.autostartEnabled === 'boolean') {
    autostartEnabled.value = payload.autostartEnabled
  }
  notify('success', 'MCP 设置请求已应用')
}

async function setMcpServerEnabled(enabled: boolean) {
  if (mcpBusy.value)
    return
  mcpBusy.value = true
  try {
    mcpStatus.value = await invoke<McpServerStatus>('set_mcp_server_enabled', { enabled })
    notify('success', enabled ? '本地 MCP 服务已开启' : '本地 MCP 服务已关闭')
  }
  catch (value) {
    notify('error', `切换 MCP 服务失败：${String(value)}`)
  }
  finally {
    mcpBusy.value = false
  }
}

function mcpConfigText() {
  const status = mcpStatus.value
  if (!status)
    return ''
  return JSON.stringify({
    mcpServers: {
      runvoke: {
        type: 'streamable-http',
        url: status.endpoint,
        headers: { Authorization: `Bearer ${status.authorizationToken}` },
      },
    },
  }, null, 2)
}

async function copyMcpConfig() {
  const config = mcpConfigText()
  if (!config)
    return
  try {
    if (navigator.clipboard?.writeText)
      await navigator.clipboard.writeText(config)
    else
      copyTextFallback(config)
    notify('success', 'MCP 配置已复制')
  }
  catch (value) {
    notify('error', `复制 MCP 配置失败：${String(value)}`)
  }
}

async function showTestNotification() {
  if (!isTauri() || notificationTesting.value)
    return
  notificationTesting.value = true
  try {
    await invoke('show_test_notification', {
      position: notificationPosition.value,
      theme: theme.value,
      stackingEnabled: notificationStackingEnabled.value,
    })
  }
  catch (value) {
    notify('error', `测试通知显示失败：${String(value)}`)
  }
  finally {
    notificationTesting.value = false
  }
}

function preventNativeContextMenu(event: MouseEvent) {
  event.preventDefault()
}

function notify(type: 'success' | 'error', message: string) {
  toast.value = { type, message }
  if (toastTimer)
    clearTimeout(toastTimer)
  toastTimer = window.setTimeout(() => {
    toast.value = null
  }, 3_200)
}

function compareVersions(left: string, right: string) {
  const parse = (version: string) => {
    const [core, prerelease = ''] = version.replace(/^v/i, '').split('-', 2)
    return {
      core: core.split('.').map(part => Number.parseInt(part, 10) || 0),
      prerelease: prerelease.split('.').filter(Boolean),
    }
  }
  const leftParts = parse(left)
  const rightParts = parse(right)
  for (let index = 0; index < Math.max(leftParts.core.length, rightParts.core.length); index += 1) {
    const difference = (leftParts.core[index] ?? 0) - (rightParts.core[index] ?? 0)
    if (difference)
      return difference
  }
  if (!leftParts.prerelease.length || !rightParts.prerelease.length)
    return leftParts.prerelease.length ? -1 : rightParts.prerelease.length ? 1 : 0
  for (let index = 0; index < Math.max(leftParts.prerelease.length, rightParts.prerelease.length); index += 1) {
    const leftIdentifier = leftParts.prerelease[index]
    const rightIdentifier = rightParts.prerelease[index]
    if (leftIdentifier === undefined)
      return -1
    if (rightIdentifier === undefined)
      return 1
    if (leftIdentifier === rightIdentifier)
      continue
    const leftNumber = Number.parseInt(leftIdentifier, 10)
    const rightNumber = Number.parseInt(rightIdentifier, 10)
    const leftIsNumber = String(leftNumber) === leftIdentifier
    const rightIsNumber = String(rightNumber) === rightIdentifier
    if (leftIsNumber && rightIsNumber)
      return leftNumber - rightNumber
    if (leftIsNumber)
      return -1
    if (rightIsNumber)
      return 1
    return leftIdentifier.localeCompare(rightIdentifier)
  }
  return 0
}

function stableUpdate(update: Update | null): AvailableUpdate | null {
  return update
    ? { channel: 'stable', update, version: update.version, body: update.body ?? '' }
    : null
}

function previewUpdate(update: PreviewUpdate | null): AvailableUpdate | null {
  return update
    ? { channel: 'preview', version: update.version, body: update.body }
    : null
}

async function checkForUpdate(showResult = false) {
  if (!isTauri() || updateInstalling.value || updateChecking.value)
    return

  updateChecking.value = true
  try {
    const stableResult = await check()
    const stable = stableUpdate(stableResult)
    let preview: AvailableUpdate | null = null
    if (previewUpdatesEnabled.value) {
      try {
        preview = previewUpdate(await invoke<PreviewUpdate | null>('check_preview_update'))
      }
      catch (value) {
        if (!stable)
          throw value
      }
    }
    availableUpdate.value = preview && (!stable || compareVersions(preview.version, stable.version) > 0)
      ? preview
      : stable
    if (availableUpdate.value && showResult) {
      notify('success', `${availableUpdate.value.channel === 'preview' ? '发现预览版本' : '发现新版本'} v${availableUpdate.value.version}`)
    }
    else if (!availableUpdate.value && showResult) {
      notify('success', '当前已是最新版本')
    }
  }
  catch (value) {
    if (showResult)
      notify('error', `检查更新失败：${String(value)}`)
  }
  finally {
    updateChecking.value = false
  }
}

function trackUpdateDownload(event: DownloadEvent) {
  if (event.event === 'Started') {
    updateProgress.value = { received: 0, total: event.data.contentLength ?? 0 }
    return
  }
  if (event.event === 'Progress')
    updateProgress.value.received += event.data.chunkLength
}

function updateProgressLabel() {
  if (busyAction.value === 'update' && runningCount.value)
    return '正在停止全部运行任务…'
  const { received, total } = updateProgress.value
  if (!total)
    return '正在下载更新…'
  return `正在下载 ${Math.min(100, Math.round(received / total * 100))}%`
}

async function installAvailableUpdate() {
  const update = availableUpdate.value
  if (!update || updateInstalling.value)
    return

  updateInstalling.value = true
  busyAction.value = 'update'
  updateProgress.value = { received: 0, total: 0 }
  try {
    await stopAllActiveRuns()
    if (update.channel === 'preview')
      await invoke('install_preview_update')
    else
      await update.update.downloadAndInstall(trackUpdateDownload)
    await relaunch()
  }
  catch (value) {
    updateInstalling.value = false
    if (busyAction.value === 'update')
      busyAction.value = ''
    notify('error', `更新安装失败：${String(value)}`)
  }
}

function openCreateForm() {
  closeGroupEditor()
  editingProject.value = null
  formOpen.value = true
}

function openSettingsPage() {
  closeGroupEditor()
  projectOpenMenuOpen.value = false
  updatePopoverOpen.value = false
  settingsOpen.value = true
}

function promptProjectImportAfterUpdate(version: string) {
  try {
    const versionStorageKey = 'runvoke:last-opened-version'
    const promptStateStorageKey = 'runvoke:project-import-prompt-state'
    const previousVersion = window.localStorage.getItem(versionStorageKey)
    const promptState = window.localStorage.getItem(promptStateStorageKey)
    window.localStorage.setItem(versionStorageKey, version)

    if (!promptState) {
      window.localStorage.setItem(promptStateStorageKey, previousVersion ? 'shown' : 'pending')
      return
    }

    if (promptState === 'pending' && previousVersion && previousVersion !== version) {
      window.localStorage.setItem(promptStateStorageKey, 'shown')
      importPromptOpen.value = true
    }
  }
  catch {
    // The import prompt is optional when local storage is unavailable.
  }
}

function normalizeDirectory(directory: string) {
  return directory.replace(/[\\/]+$/, '').toLocaleLowerCase()
}

async function loadProjectImportCandidates() {
  if (!isTauri() || projectImportLoading.value)
    return

  projectImportLoading.value = true
  try {
    const command = projectImportSource.value === 'cursor' ? 'list_cursor_projects' : 'list_vscode_projects'
    const candidates = await invoke<ImportedProject[]>(command)
    const existingDirectories = new Set(projects.value.map(project => normalizeDirectory(project.directory)))
    projectImportCandidates.value = candidates.filter(project => !existingDirectories.has(normalizeDirectory(project.directory)))
  }
  catch (value) {
    projectImportCandidates.value = []
    const sourceLabel = projectImportSource.value === 'cursor' ? 'Cursor' : 'Visual Studio Code'
    notify('error', `读取 ${sourceLabel} 项目失败：${String(value)}`)
  }
  finally {
    projectImportLoading.value = false
  }
}

function openProjectImportDialog() {
  importPromptOpen.value = false
  projectImportDialogOpen.value = true
  void loadProjectImportCandidates()
}

function openAgentProjectImportDialog(candidates: ImportedProject[]) {
  const existingDirectories = new Set(projects.value.map(project => normalizeDirectory(project.directory)))
  agentProjectImportCandidates.value = candidates.filter(project => !existingDirectories.has(normalizeDirectory(project.directory)))
  agentProjectImportDialogOpen.value = true
}

function isAgentImportCandidate(candidate: unknown): candidate is ImportedProject {
  if (!candidate || typeof candidate !== 'object')
    return false
  const value = candidate as Partial<ImportedProject>
  return typeof value.name === 'string'
    && Boolean(value.name.trim())
    && typeof value.directory === 'string'
    && Boolean(value.directory.trim())
    && (value.suggestedCommand === null || value.suggestedCommand === undefined || typeof value.suggestedCommand === 'string')
}

async function importProjects(candidates: ImportedProject[]) {
  if (projectImporting.value || !candidates.length)
    return

  projectImporting.value = true
  let imported = 0
  let failed = 0
  try {
    const existingDirectories = new Set(projects.value.map(project => normalizeDirectory(project.directory)))
    for (const candidate of candidates) {
      if (existingDirectories.has(normalizeDirectory(candidate.directory)))
        continue

      const command = candidate.suggestedCommand || 'pnpm dev'
      const project: ProjectConfig = {
        id: '',
        name: candidate.name,
        directory: candidate.directory,
        groupId: null,
        command,
        env: [],
        port: null,
        tasks: [{ id: '', name: '开发服务器', command, mode: 'service' }],
      }
      try {
        await saveProject(project)
        existingDirectories.add(normalizeDirectory(candidate.directory))
        imported += 1
      }
      catch {
        failed += 1
      }
    }
    if (imported) {
      projectImportDialogOpen.value = false
      notify('success', failed ? `已导入 ${imported} 个项目，${failed} 个项目未能导入` : `已导入 ${imported} 个项目`)
    }
    else {
      notify('error', failed ? '没有项目导入成功，请检查项目目录和配置' : '所选项目已存在')
    }
  }
  finally {
    projectImporting.value = false
  }
}

async function importAgentProjects(candidates: ImportedProject[]) {
  if (agentProjectImporting.value || !candidates.length)
    return

  agentProjectImporting.value = true
  let imported = 0
  let failed = 0
  try {
    const existingDirectories = new Set(projects.value.map(project => normalizeDirectory(project.directory)))
    for (const candidate of candidates) {
      if (existingDirectories.has(normalizeDirectory(candidate.directory)))
        continue

      const command = candidate.suggestedCommand || 'pnpm dev'
      const project: ProjectConfig = {
        id: '',
        name: candidate.name,
        directory: candidate.directory,
        groupId: null,
        command,
        env: [],
        port: null,
        tasks: [{ id: '', name: '开发服务器', command, mode: 'service' }],
      }
      try {
        await saveProject(project)
        existingDirectories.add(normalizeDirectory(candidate.directory))
        imported += 1
      }
      catch {
        failed += 1
      }
    }
    if (imported) {
      agentProjectImportDialogOpen.value = false
      notify('success', failed ? `已按确认纳入 ${imported} 个项目，${failed} 个项目未能纳入` : `已按确认纳入 ${imported} 个项目`)
    }
    else {
      notify('error', failed ? '没有项目纳入成功，请检查项目目录和配置' : '所选项目已存在')
    }
  }
  finally {
    agentProjectImporting.value = false
  }
}

async function openProjectConfigDirectory() {
  if (!isTauri() || projectConfigOpening.value)
    return

  projectConfigOpening.value = true
  try {
    await invoke('open_project_config_directory')
    notify('success', '已在文件管理器中打开项目配置目录')
  }
  catch (value) {
    notify('error', `打开项目配置目录失败：${String(value)}`)
  }
  finally {
    projectConfigOpening.value = false
  }
}

function openEditForm() {
  editingProject.value = selectedProject.value
  formOpen.value = true
}

function showProjectContextMenu(event: MouseEvent, project: ProjectConfig) {
  event.preventDefault()
  selectedId.value = project.id
  projectContextSubmenuOpen.value = false
  if (projectContextSubmenuCloseTimer)
    clearTimeout(projectContextSubmenuCloseTimer)

  const target = event.currentTarget
  const rect = target instanceof HTMLElement ? target.getBoundingClientRect() : null
  const anchorX = event.clientX || rect?.left || 12
  const anchorY = event.clientY || rect?.bottom || 12
  const menuWidth = 144
  const menuHeight = 76
  const submenuWidth = 178
  const submenuHeight = Math.min(320, 84 + projectGroups.value.length * 32)
  const viewportPadding = 12
  const left = Math.max(viewportPadding, Math.min(anchorX, window.innerWidth - menuWidth - viewportPadding))
  const top = Math.max(viewportPadding, Math.min(anchorY, window.innerHeight - menuHeight - viewportPadding))
  const submenuLeft = left + menuWidth + submenuWidth <= window.innerWidth - viewportPadding
    ? left + menuWidth - 1
    : left - submenuWidth + 1
  projectContextMenu.value = {
    project,
    left,
    top,
    submenuLeft: Math.max(viewportPadding, submenuLeft),
    submenuTop: Math.max(viewportPadding, Math.min(top, window.innerHeight - submenuHeight - viewportPadding)),
  }
  void nextTick(() => contextMenuEditButton.value?.focus())
}

function closeProjectContextMenu() {
  if (projectContextSubmenuCloseTimer)
    clearTimeout(projectContextSubmenuCloseTimer)
  projectContextSubmenuOpen.value = false
  projectContextMenu.value = null
}

function openProjectContextSubmenu() {
  if (projectContextSubmenuCloseTimer)
    clearTimeout(projectContextSubmenuCloseTimer)
  projectContextSubmenuOpen.value = true
}

function scheduleProjectContextSubmenuClose() {
  if (projectContextSubmenuCloseTimer)
    clearTimeout(projectContextSubmenuCloseTimer)
  projectContextSubmenuCloseTimer = window.setTimeout(() => {
    projectContextSubmenuOpen.value = false
  }, 180)
}

function toggleProjectContextSubmenu() {
  if (projectContextSubmenuOpen.value) {
    projectContextSubmenuOpen.value = false
    return
  }
  openProjectContextSubmenu()
}

function editProjectFromContextMenu() {
  const project = projectContextMenu.value?.project
  if (!project)
    return
  closeProjectContextMenu()
  editingProject.value = project
  formOpen.value = true
}

function selectProject(projectId: string) {
  selectedId.value = projectId
  activeGroupMenuId.value = null
  settingsOpen.value = false
}

function openCreateGroup(projectId = '') {
  groupDraftId.value = ''
  groupDraftName.value = ''
  groupDraftProjectId.value = projectId
  groupEditorOpen.value = true
  activeGroupMenuId.value = null
  void nextTick(() => groupNameInput.value?.focus())
}

function openRenameGroup(group: ProjectGroup | undefined) {
  if (!group)
    return
  groupDraftId.value = group.id
  groupDraftName.value = group.name
  groupDraftProjectId.value = ''
  groupEditorOpen.value = true
  activeGroupMenuId.value = null
  void nextTick(() => groupNameInput.value?.select())
}

function closeGroupEditor() {
  groupEditorOpen.value = false
  groupDraftId.value = ''
  groupDraftName.value = ''
  groupDraftProjectId.value = ''
}

async function handleSaveGroup() {
  const name = groupDraftName.value.trim()
  if (!name || groupSaving.value)
    return
  groupSaving.value = true
  try {
    const creating = !groupDraftId.value
    const projectId = creating ? groupDraftProjectId.value : ''
    const existing = projectGroups.value.find(group => group.id === groupDraftId.value)
    const saved = await saveProjectGroup({ id: groupDraftId.value, name, collapsed: existing?.collapsed ?? false })
    closeGroupEditor()
    if (projectId) {
      try {
        const targetIndex = projects.value.filter(project => project.groupId === saved.id && project.id !== projectId).length
        await moveProject(projectId, saved.id, targetIndex)
      }
      catch (value) {
        notify('error', `分组已创建，但移动项目失败：${String(value)}`)
        return
      }
    }
    notify('success', creating ? (projectId ? '分组已创建，项目已移入' : '分组已创建') : '分组已重命名')
  }
  catch (value) {
    notify('error', String(value))
  }
  finally {
    groupSaving.value = false
  }
}

function requestInstallAvailableUpdate(event: MouseEvent) {
  if (busyAction.value) {
    notify('error', '请等待当前操作完成后再安装更新')
    return
  }
  const count = runningCount.value
  const previewNotice = availableUpdate.value?.channel === 'preview'
    ? '这是预览版本，可能包含未完成或不稳定的功能。'
    : ''
  requestConfirmation(
    event,
    count
      ? `${previewNotice}安装更新前将停止所有项目当前运行的 ${count} 个任务，并在安装完成后重启 Runvoke。是否继续？`
      : `${previewNotice}安装更新后将重启 Runvoke。当前没有运行中的任务，是否继续？`,
    count ? '停止全部并更新' : '确认更新',
    installAvailableUpdate,
    'primary',
  )
}

async function toggleProjectGroup(section: ProjectGroupSection) {
  activeGroupMenuId.value = null
  if (search.value.trim())
    return
  if (!section.id) {
    ungroupedCollapsed.value = !ungroupedCollapsed.value
    return
  }
  try {
    await setProjectGroupCollapsed(section.id, !section.collapsed)
  }
  catch (value) {
    notify('error', `保存折叠状态失败：${String(value)}`)
  }
}

function createGroupFromContextMenu() {
  const projectId = projectContextMenu.value?.project.id
  if (!projectId)
    return
  closeProjectContextMenu()
  openCreateGroup(projectId)
}

async function moveContextProjectToGroup(groupId: string | null) {
  const project = projectContextMenu.value?.project
  if (!project)
    return
  closeProjectContextMenu()
  if (project.groupId === groupId)
    return

  const targetIndex = projects.value.filter(item => item.id !== project.id && item.groupId === groupId).length
  const groupName = groupId ? projectGroups.value.find(group => group.id === groupId)?.name : '未分组'
  await perform(
    `project-group-${project.id}`,
    () => moveProject(project.id, groupId, targetIndex),
    `已移到“${groupName ?? '未分组'}”`,
  )
}

async function handleRemoveGroup(group: ProjectGroup | undefined) {
  if (!group)
    return
  activeGroupMenuId.value = null
  if (!window.confirm(`删除分组“${group.name}”吗？组内项目将移到“未分组”。`))
    return
  await perform(`group-delete-${group.id}`, () => removeProjectGroup(group.id), '分组已删除，项目已移到未分组')
}

async function handleProjectMove(payload: { projectId: string, groupId: string | null, targetIndex: number }) {
  const visibleProjectIds = new Set(filteredProjects.value.map(project => project.id))
  const targetProjects = projects.value.filter(project => project.id !== payload.projectId && project.groupId === payload.groupId)
  const visibleTargets = targetProjects.filter(project => visibleProjectIds.has(project.id))
  const nextVisibleProject = visibleTargets[payload.targetIndex]
  const previousVisibleProject = visibleTargets[payload.targetIndex - 1]
  const targetIndex = nextVisibleProject
    ? targetProjects.findIndex(project => project.id === nextVisibleProject.id)
    : previousVisibleProject
      ? targetProjects.findIndex(project => project.id === previousVisibleProject.id) + 1
      : targetProjects.length
  try {
    await moveProject(payload.projectId, payload.groupId, targetIndex)
  }
  catch (value) {
    notify('error', `保存项目位置失败：${String(value)}`)
  }
}

async function handleSave(project: ProjectConfig) {
  saving.value = true
  try {
    await saveProject(project)
    formOpen.value = false
    settingsOpen.value = false
    notify('success', project.id ? '项目配置已更新' : '项目已接入启动器')
  }
  catch (value) {
    notify('error', String(value))
  }
  finally {
    saving.value = false
  }
}

async function perform(label: string, action: () => Promise<unknown>, success: string) {
  busyAction.value = label
  try {
    await action()
    notify('success', success)
  }
  catch (value) {
    notify('error', String(value))
  }
  finally {
    busyAction.value = ''
  }
}

function handleDelete(event: MouseEvent) {
  const project = selectedProject.value
  if (!project)
    return
  requestConfirmation(
    event,
    `确定删除“${project.name}”吗？运行中的进程也会被停止。`,
    '确认删除',
    () => perform('delete', () => removeProject(project.id), '项目已删除'),
  )
}

function requestInstallAvailableUpdateFromMcp() {
  if (!availableUpdate.value) {
    notify('error', '当前没有可安装的更新，请先检查更新')
    return
  }
  if (busyAction.value) {
    notify('error', '请等待当前操作完成后再安装更新')
    return
  }
  const count = runningCount.value
  const previewNotice = availableUpdate.value?.channel === 'preview'
    ? '这是预览版本，可能包含未完成或不稳定的功能。'
    : ''
  const width = Math.min(280, window.innerWidth - 24)
  const right = window.innerWidth - 24
  requestConfirmationAt(
    { top: window.innerHeight - 68, bottom: window.innerHeight - 34, left: right - 132, right },
    count
      ? `${previewNotice}安装更新前将停止所有项目当前运行的 ${count} 个任务，并在安装完成后重启 Runvoke。是否继续？`
      : `${previewNotice}安装更新后将重启 Runvoke。当前没有运行中的任务，是否继续？`,
    count ? '停止全部并更新' : '确认更新',
    installAvailableUpdate,
    'primary',
    width,
  )
}

function updateScrollbar(container: HTMLElement | null, scrollbar: ScrollbarState) {
  if (!container)
    return

  const visibleHeight = container.clientHeight
  const scrollableHeight = container.scrollHeight
  const overflow = scrollableHeight > visibleHeight + 1
  const thumbHeight = overflow
    ? Math.min(visibleHeight, Math.max(28, Math.round(visibleHeight * visibleHeight / scrollableHeight)))
    : 0
  const scrollRange = scrollableHeight - visibleHeight
  const trackRange = visibleHeight - thumbHeight
  const thumbTop = overflow && trackRange
    ? Math.round(container.scrollTop * trackRange / scrollRange)
    : 0

  if (scrollbar.overflow !== overflow)
    scrollbar.overflow = overflow
  if (scrollbar.thumbHeight !== thumbHeight)
    scrollbar.thumbHeight = thumbHeight
  if (scrollbar.thumbTop !== thumbTop)
    scrollbar.thumbTop = thumbTop
}

function updateRunListScrollbar() {
  updateScrollbar(runListContainer.value, runListScrollbar.value)
}

function updateLogScrollbar() {
  updateScrollbar(logContainer.value, logScrollbar.value)
}

function updateProjectListScrollbar() {
  updateScrollbar(projectListContainer.value, projectListScrollbar.value)
}

function updateScrollbars() {
  updateRunListScrollbar()
  updateLogScrollbar()
  updateProjectListScrollbar()
}

function scheduleScrollbarUpdate() {
  if (scrollbarUpdateFrame !== undefined)
    return

  scrollbarUpdateFrame = requestAnimationFrame(() => {
    scrollbarUpdateFrame = undefined
    updateScrollbars()
  })
}

function setScrollbarHover(scrollbar: ScrollbarState, hovered: boolean) {
  scrollbar.hovered = hovered
  if (hovered)
    void nextTick(updateScrollbars)
}

function startScrollbarDrag(kind: 'projects' | 'runs' | 'logs', event: PointerEvent) {
  const container = kind === 'projects'
    ? projectListContainer.value
    : kind === 'runs' ? runListContainer.value : logContainer.value
  const scrollbar = kind === 'projects'
    ? projectListScrollbar.value
    : kind === 'runs' ? runListScrollbar.value : logScrollbar.value
  if (!container || !scrollbar.overflow)
    return

  event.preventDefault()
  const target = event.currentTarget
  if (target instanceof HTMLElement)
    target.setPointerCapture(event.pointerId)
  draggingScrollbar = { container, scrollbar, startY: event.clientY, startScrollTop: container.scrollTop }
}

function dragScrollbar(event: PointerEvent) {
  if (!draggingScrollbar)
    return

  const { container, scrollbar, startY, startScrollTop } = draggingScrollbar
  const trackRange = container.clientHeight - scrollbar.thumbHeight
  const scrollRange = container.scrollHeight - container.clientHeight
  if (trackRange > 0 && scrollRange > 0)
    container.scrollTop = Math.min(scrollRange, Math.max(0, startScrollTop + (event.clientY - startY) * scrollRange / trackRange))
  updateScrollbar(container, scrollbar)
}

function stopScrollbarDrag(event: PointerEvent) {
  const target = event.currentTarget
  if (target instanceof HTMLElement && target.hasPointerCapture(event.pointerId))
    target.releasePointerCapture(event.pointerId)
  draggingScrollbar = null
}

function requestConfirmation(
  event: MouseEvent,
  message: string,
  confirmLabel: string,
  action: () => Promise<void>,
  variant: PendingConfirmation['variant'] = 'danger',
) {
  const target = event.currentTarget
  if (!(target instanceof HTMLElement))
    return

  const rect = target.getBoundingClientRect()
  requestConfirmationAt(rect, message, confirmLabel, action, variant)
}

function requestConfirmationAt(
  rect: Pick<DOMRect, 'top' | 'bottom' | 'left' | 'right'>,
  message: string,
  confirmLabel: string,
  action: () => Promise<void>,
  variant: PendingConfirmation['variant'] = 'danger',
  requestedWidth?: number,
) {
  const viewportPadding = 12
  const popoverWidth = requestedWidth ?? Math.min(280, window.innerWidth - viewportPadding * 2)
  const estimatedHeight = 142
  const offset = 8
  const below = rect.bottom + offset
  const top = below + estimatedHeight <= window.innerHeight - viewportPadding
    ? below
    : Math.max(viewportPadding, rect.top - offset - estimatedHeight)
  const left = Math.min(
    Math.max(viewportPadding, rect.right - popoverWidth),
    window.innerWidth - popoverWidth - viewportPadding,
  )

  pendingConfirmation.value = { message, confirmLabel, variant, top, left, width: popoverWidth, action }
  void nextTick(() => confirmationCancelButton.value?.focus())
}

async function executePendingConfirmation() {
  const confirmation = pendingConfirmation.value
  if (!confirmation)
    return
  pendingConfirmation.value = null
  await confirmation.action()
}

function isRunActive(state: string) {
  return ['starting', 'running', 'stopping'].includes(state)
}

async function stopAllActiveRuns() {
  const runs = activeRuns.value
  if (!runs.length)
    return 0

  const runIds = runs.filter(run => run.state !== 'stopping').map(run => run.runId)
  const results = await Promise.allSettled(runIds.map(runId => stopRunWithoutDesktopNotification(runId)))
  const failures = results.flatMap(result => result.status === 'rejected' ? [String(result.reason)] : [])
  if (failures.length)
    throw new Error(`${failures.length} 个任务停止失败：${failures[0]}`)

  const deadline = Date.now() + 15_000
  while (activeRuns.value.length) {
    if (Date.now() >= deadline)
      throw new Error('等待全部任务停止超时，请检查运行日志后重试')
    await new Promise<void>(resolve => window.setTimeout(resolve, 100))
  }
  return runs.length
}

async function handleStopAllActiveRuns() {
  if (busyAction.value)
    return
  busyAction.value = 'stop-all'
  try {
    const count = await stopAllActiveRuns()
    notify('success', `已停止 ${count} 个运行任务`)
  }
  catch (value) {
    notify('error', String(value))
  }
  finally {
    if (busyAction.value === 'stop-all')
      busyAction.value = ''
  }
}

function confirmStopAllActiveRuns(event: MouseEvent) {
  const count = runningCount.value
  if (!count)
    return
  requestConfirmation(
    event,
    `确定停止所有项目当前运行的 ${count} 个任务吗？相关子进程也会一并回收。`,
    '停止全部',
    handleStopAllActiveRuns,
  )
}

function projectState(projectId: string) {
  return Object.values(runsById.value).find((run) => run.projectId === projectId && isRunActive(run.state))?.state ?? 'stopped'
}

function taskModeLabel(mode: string) {
  return mode === 'service' ? '常驻' : '一次'
}

function activeRunForTask(taskId: string): RuntimeStatus | null {
  const project = selectedProject.value
  if (!project)
    return null
  return Object.values(runsById.value).find((run) =>
    run.projectId === project.id && run.taskId === taskId && isRunActive(run.state),
  ) ?? null
}

function confirmStopRun(event: MouseEvent, run: RuntimeStatus) {
  requestConfirmation(
    event,
    `确定停止「${run.taskName}」吗？相关子进程也会被停止。`,
    '停止任务',
    () => perform(`stop-${run.runId}`, () => stopRunWithoutDesktopNotification(run.runId), '任务已停止'),
  )
}

async function handleTaskAction(event: MouseEvent, task: ProjectTask) {
  if (operationsLocked.value)
    return
  const activeRun = activeRunForTask(task.id)
  if (activeRun) {
    confirmStopRun(event, activeRun)
    return
  }
  await perform(`task-${task.id}`, () => runTask(selectedProject.value!.id, task), `已开始执行「${task.name}」`)
}

function handleDismissRun(event: MouseEvent, run: RuntimeStatus) {
  requestConfirmation(
    event,
    `移除「${run.taskName}」的运行记录和日志吗？此操作不可恢复。`,
    '移除记录',
    () => perform(`dismiss-${run.runId}`, () => dismissRun(run.runId), '运行记录已移除'),
  )
}

function handleDismissInactiveRuns(event: MouseEvent) {
  const count = inactiveRunCount.value
  if (!count)
    return
  requestConfirmation(
    event,
    `确定移除 ${count} 条已结束的运行记录和日志吗？此操作不可恢复。`,
    '清除记录',
    () => perform('dismiss-inactive', () => dismissInactiveRuns(), `已移除 ${count} 条运行记录`),
  )
}

async function handleTemporaryCommand() {
  const project = selectedProject.value
  const command = temporaryCommand.value.trim()
  if (!project || !command || operationsLocked.value)
    return
  await perform('temporary', () => runTemporaryCommand(project.id, command), '临时命令已开始执行')
  temporaryCommand.value = ''
}

async function handleProjectOpen(action: 'vscode' | 'file-manager') {
  projectOpenMenuOpen.value = false
  const project = selectedProject.value
  if (!project)
    return
  if (action === 'vscode')
    await perform('open-vscode', () => openInVscode(project.directory), '已交给 VS Code 打开')
  if (action === 'file-manager')
    await perform('open-file-manager', () => openInFileManager(project.directory), '已在文件管理器中打开')
}

function closeProjectOpenMenu(event: FocusEvent) {
  const nextTarget = event.relatedTarget
  if (nextTarget instanceof Node && (event.currentTarget as HTMLElement).contains(nextTarget))
    return
  projectOpenMenuOpen.value = false
}

async function toggleAutostart() {
  const next = !autostartEnabled.value
  await perform('autostart', () => setAutostart(next), next ? '已启用开机启动' : '已关闭开机启动')
}

function formatTime(timestamp: number) {
  return new Intl.DateTimeFormat('zh-CN', {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false,
  }).format(timestamp)
}

function shortPath(path: string) {
  const parts = path.split(/[\\/]/).filter(Boolean)
  return parts.length > 3 ? `…/${parts.slice(-3).join('/')}` : path
}

function stateLabel(state?: string) {
  return {
    starting: '启动中',
    running: '运行中',
    stopping: '停止中',
    stopped: '已停止',
    succeeded: '已完成',
    failed: '失败',
  }[state ?? 'stopped'] ?? '已停止'
}
</script>

<template>
  <TooltipProvider :delay-duration="420" :skip-delay-duration="180">
  <div class="app-root" :class="{ 'theme-dark': theme === 'dark' }" @contextmenu.prevent>
    <ResizableSplitPane
      class="app-frame"
      :disabled="settingsOpen"
      :initial-start-size="292"
      :min-start-size="220"
      :min-end-size="580"
      :max-start-size="420"
      storage-key="runvoke:workspace-sidebar-width"
      label="调整项目侧栏宽度"
    >
      <template #start>
        <aside class="sidebar">
      <header class="brand-row">
        <img class="brand-mark" :src="brandIcon" alt="" aria-hidden="true" />
        <div>
          <strong>Runvoke</strong>
          <span>本地开发工作台</span>
        </div>
        <button
          class="settings-button"
          :class="{ active: settingsOpen }"
          type="button"
          :aria-current="settingsOpen ? 'page' : undefined"
          @click="openSettingsPage"
        >设置</button>
      </header>

      <div class="fleet-heading">
        <div>
          <span>我的项目</span>
          <b>{{ runningCount ? `${runningCount} 个任务运行中` : '当前没有运行任务' }} · 共 {{ projects.length }} 个项目</b>
        </div>
        <div class="fleet-actions">
          <button
            class="stop-all-button"
            type="button"
            aria-label="停止全部项目任务"
            title="停止全部项目任务"
            :disabled="!runningCount || Boolean(busyAction) || updateInstalling"
            @click="confirmStopAllActiveRuns"
          ><i aria-hidden="true" /></button>
          <button class="add-button" type="button" aria-label="添加项目" title="添加项目" @click="openCreateForm">+</button>
        </div>
      </div>

      <Transition name="settings">
        <form v-if="groupEditorOpen" class="group-editor" @submit.prevent="handleSaveGroup">
          <input ref="groupNameInput" v-model="groupDraftName" maxlength="30" :placeholder="groupDraftId ? '重命名分组' : '输入分组名称'" />
          <button type="submit" :disabled="!groupDraftName.trim() || groupSaving">{{ groupSaving ? '保存中' : '保存' }}</button>
          <button type="button" aria-label="取消" @click="closeGroupEditor">×</button>
        </form>
      </Transition>

      <label class="search-box">
        <span aria-hidden="true">搜索</span>
        <input v-model="search" placeholder="搜索项目、目录或命令" />
      </label>

      <div class="scroll-fade-wrap project-list-scroll-wrap" @mouseenter="setScrollbarHover(projectListScrollbar, true)" @mouseleave="setScrollbarHover(projectListScrollbar, false)">
        <nav ref="projectListContainer" class="project-list" aria-label="项目列表" @scroll="updateProjectListScrollbar">
          <section v-for="section in projectGroupSections" :key="section.id ?? 'ungrouped'" class="project-group-section">
            <header v-if="section.id || projectGroups.length" class="project-group-heading">
              <button
                class="project-group-toggle"
                type="button"
                :aria-expanded="!section.collapsed"
                :title="search.trim() ? '搜索时保持展开' : section.collapsed ? '展开分组' : '收起分组'"
                @click="toggleProjectGroup(section)"
              >
                <i :class="{ collapsed: section.collapsed }" />
                <OverflowTooltip as="strong" :text="section.name">{{ section.name }}</OverflowTooltip>
                <small>{{ section.projects.length }}</small>
              </button>
              <button
                v-if="section.id"
                class="project-group-more"
                type="button"
                aria-label="分组操作"
                :aria-expanded="activeGroupMenuId === section.id"
                @click="activeGroupMenuId = activeGroupMenuId === section.id ? null : section.id"
              >···</button>
            </header>
            <div v-if="section.id && activeGroupMenuId === section.id" class="project-group-actions">
              <button type="button" @click="openRenameGroup(projectGroups.find(group => group.id === section.id))">重命名</button>
              <button class="danger" type="button" @click="handleRemoveGroup(projectGroups.find(group => group.id === section.id))">删除分组</button>
            </div>
            <ProjectGroupList
              v-if="!section.collapsed"
              :key="`${section.id ?? 'ungrouped'}:${section.projects.map(project => project.id).join(',')}`"
              :group-id="section.id"
              :projects="section.projects"
              @move="handleProjectMove"
            >
              <template #default="{ project }">
                <button
                  class="project-item reorderable"
                  :class="{
                    selected: selectedId === project.id,
                    active: isRunActive(projectState(project.id)),
                  }"
                  :data-project-id="project.id"
                  type="button"
                  @click="selectProject(project.id)"
                  @contextmenu="showProjectContextMenu($event, project)"
                >
                  <span class="status-beacon" :class="projectState(project.id)"><i /></span>
                  <span class="project-copy">
                    <OverflowTooltip as="strong" :text="project.name">{{ project.name }}</OverflowTooltip>
                    <span class="project-detail-row">
                      <OverflowTooltip as="small" :text="project.directory">{{ shortPath(project.directory) }}</OverflowTooltip>
                      <span v-if="project.port" class="port-tag">:{{ project.port }}</span>
                    </span>
                  </span>
                </button>
              </template>
            </ProjectGroupList>
          </section>

          <div v-if="!loading && !projectGroupSections.length" class="list-empty">
            <p>{{ projects.length ? '没有匹配的项目' : '还没有接入项目' }}</p>
            <button v-if="!projects.length" type="button" @click="openCreateForm">添加第一个项目</button>
          </div>
        </nav>
        <span v-if="projectListScrollbar.overflow" class="scrollbar-rail light-scrollbar" :class="{ visible: projectListScrollbar.hovered }">
          <i
            :style="{ height: `${projectListScrollbar.thumbHeight}px`, transform: `translateY(${projectListScrollbar.thumbTop}px)` }"
            @pointerdown="startScrollbarDrag('projects', $event)"
            @pointermove="dragScrollbar"
            @pointerup="stopScrollbarDrag"
            @pointercancel="stopScrollbarDrag"
          />
        </span>
      </div>

      <footer class="sidebar-footer">
        <span class="tray-dot" />
        <OverflowTooltip :text="'应用将在系统托盘中保持运行'">应用将在系统托盘中保持运行</OverflowTooltip>
        <Tooltip v-if="githubLinkVisible">
          <TooltipTrigger as-child>
            <button
              class="github-link-button"
              type="button"
              aria-label="在默认浏览器中打开 Runvoke GitHub 仓库"
              @click="openRepository"
            >
              <svg aria-hidden="true" viewBox="0 0 16 16" focusable="false">
                <path d="M8 1.1a6.9 6.9 0 0 0-2.18 13.45c.35.06.48-.15.48-.34v-1.2c-1.95.42-2.36-.83-2.36-.83-.32-.81-.79-1.03-.79-1.03-.65-.44.05-.43.05-.43.72.05 1.1.74 1.1.74.64 1.1 1.68.78 2.08.6.06-.46.25-.78.46-.96-1.56-.18-3.2-.78-3.2-3.47 0-.77.27-1.4.73-1.89-.07-.18-.32-.9.07-1.87 0 0 .6-.19 1.96.72A6.7 6.7 0 0 1 8 3.62c.55 0 1.1.07 1.61.22 1.36-.91 1.96-.72 1.96-.72.39.97.14 1.69.07 1.87.46.49.73 1.12.73 1.89 0 2.7-1.65 3.29-3.22 3.46.25.22.48.65.48 1.31v1.94c0 .19.13.4.49.34A6.9 6.9 0 0 0 8 1.1Z" />
              </svg>
            </button>
          </TooltipTrigger>
          <TooltipContent class="app-tooltip-content" side="top" :side-offset="6">打开 GitHub 仓库</TooltipContent>
        </Tooltip>
        <button
          v-if="availableUpdate"
          class="update-trigger"
          type="button"
          :aria-expanded="updatePopoverOpen"
          :disabled="updateInstalling"
          @click="updatePopoverOpen = !updatePopoverOpen"
        ><i />更新</button>
        <span v-else-if="appVersion" class="version-label">v{{ appVersion }}</span>
          <Transition name="update-popover">
            <section v-if="updatePopoverOpen && availableUpdate" class="update-popover" @keydown.esc="updatePopoverOpen = false">
              <span>{{ availableUpdate.channel === 'preview' ? '发现预览版本' : '发现新版本' }}</span>
              <strong>v{{ availableUpdate.version }}</strong>
              <small v-if="availableUpdate.channel === 'preview'" class="preview-update-notice">预览版本可能包含未完成或不稳定的功能。</small>
              <OverflowTooltip as="p" :text="availableUpdate.body || '已准备好下载并安装最新版本。'">{{ availableUpdate.body || '已准备好下载并安装最新版本。' }}</OverflowTooltip>
            <small v-if="updateInstalling">{{ updateProgressLabel() }}</small>
            <div>
              <button type="button" :disabled="updateInstalling || updateChecking" @click="checkForUpdate(true)">重新检查</button>
              <button class="update-install-button" type="button" :disabled="updateInstalling" @click="requestInstallAvailableUpdate">{{ updateInstalling ? '正在安装' : '下载并安装' }}</button>
            </div>
          </section>
        </Transition>
      </footer>
        </aside>
      </template>

      <template #end>
        <main class="workspace">
      <SettingsPage
        v-if="settingsOpen"
        :autostart-enabled="autostartEnabled"
        :autostart-busy="busyAction === 'autostart'"
        :theme="theme"
        :log-link-action="logLinkAction"
        :github-link-visible="githubLinkVisible"
          :app-version="appVersion"
          :available-update-version="availableUpdate?.version ?? ''"
          :available-update-body="availableUpdate?.body ?? ''"
          :available-update-preview="availableUpdate?.channel === 'preview'"
          :preview-updates-enabled="previewUpdatesEnabled"
        :update-checking="updateChecking"
        :update-installing="updateInstalling"
        :update-progress-label="updateProgressLabel()"
        :project-config-opening="projectConfigOpening"
        :project-import-source="projectImportSource"
        :project-import-busy="projectImportLoading || projectImporting"
        :mcp-status="mcpStatus"
        :mcp-busy="mcpBusy"
        :mcp-config-text="mcpConfigText()"
        :notification-position="notificationPosition"
        :notification-stacking-enabled="notificationStackingEnabled"
        :notification-testing="notificationTesting"
        @close="settingsOpen = false"
        @toggle-autostart="toggleAutostart"
        @set-theme="theme = $event"
        @set-log-link-action="logLinkAction = $event"
          @set-github-link-visible="githubLinkVisible = $event"
          @set-preview-updates-enabled="previewUpdatesEnabled = $event"
        @check-update="checkForUpdate(true)"
        @install-update="requestInstallAvailableUpdate"
        @open-project-config-directory="openProjectConfigDirectory"
        @set-project-import-source="projectImportSource = $event"
        @open-project-import="openProjectImportDialog"
        @set-mcp-enabled="setMcpServerEnabled"
        @copy-mcp-config="copyMcpConfig"
        @set-notification-position="notificationPosition = $event"
        @set-notification-stacking-enabled="notificationStackingEnabled = $event"
        @test-notification="showTestNotification"
      />

      <template v-else-if="selectedProject">
        <header class="workspace-header">
          <div class="project-title">
            <span class="section-kicker">当前项目</span>
            <h1>{{ selectedProject.name }}</h1>
            <OverflowTooltip as="code" :text="selectedProject.directory">{{ selectedProject.directory }}</OverflowTooltip>
          </div>
          <div class="header-actions">
            <span class="project-open-control" :class="{ open: projectOpenMenuOpen }" @focusout="closeProjectOpenMenu" @keydown.esc="projectOpenMenuOpen = false">
              <button class="project-open-trigger" type="button" aria-haspopup="menu" :aria-expanded="projectOpenMenuOpen" @click="projectOpenMenuOpen = !projectOpenMenuOpen">打开项目</button>
              <span v-if="projectOpenMenuOpen" class="project-open-menu" role="menu">
                <button type="button" role="menuitem" @click="handleProjectOpen('vscode')">用 VS Code 打开</button>
                <button type="button" role="menuitem" @click="handleProjectOpen('file-manager')">在文件管理器中打开</button>
              </span>
            </span>
            <button type="button" @click="openEditForm">编辑</button>
            <button class="danger-link" type="button" @click="handleDelete($event)">删除</button>
          </div>
        </header>

        <div class="project-workspace">
          <section class="task-deck">
            <header class="task-deck-header">
              <div>
                <span class="section-kicker">项目任务</span>
                <h2>选择要执行的命令</h2>
              </div>
              <button type="button" @click="openEditForm">管理任务</button>
            </header>
            <div class="task-grid">
              <button
                v-for="task in visibleTasks"
                :key="task.id"
                class="task-launch"
                :class="{ running: activeRunForTask(task.id)?.state === 'running', transitioning: Boolean(activeRunForTask(task.id) && activeRunForTask(task.id)?.state !== 'running') }"
                type="button"
                :disabled="operationsLocked || busyAction === `task-${task.id}` || Boolean(activeRunForTask(task.id) && busyAction === `stop-${activeRunForTask(task.id)!.runId}`)"
                @click="handleTaskAction($event, task)"
              >
                <span class="task-kind">{{ taskModeLabel(task.mode) }}</span>
                <span class="task-copy">
                  <OverflowTooltip as="strong" :text="task.name">{{ task.name }}</OverflowTooltip>
                  <OverflowTooltip as="code" :text="task.command">{{ task.command }}</OverflowTooltip>
                  <small v-if="activeRunForTask(task.id)" class="task-running"><i />{{ stateLabel(activeRunForTask(task.id)?.state) }}</small>
                </span>
                <span class="task-action" aria-hidden="true" />
              </button>
            </div>
            <form class="temporary-command" @submit.prevent="handleTemporaryCommand">
              <span>$</span>
              <input v-model="temporaryCommand" aria-label="临时命令" placeholder="输入一次性临时命令，例如 pnpm build" />
              <button type="submit" :disabled="!temporaryCommand.trim() || operationsLocked || busyAction === 'temporary'">执行</button>
            </form>
          </section>

          <section class="run-workbench">
            <aside class="run-list-panel">
              <header>
                <span>运行记录</span>
                <div class="run-list-actions">
                  <button v-if="inactiveRunCount" class="run-clear-finished" type="button" :disabled="busyAction === 'dismiss-inactive'" @click="handleDismissInactiveRuns($event)">清除已结束</button>
                  <b>{{ projectRuns.length }}</b>
                </div>
              </header>
              <div class="scroll-fade-wrap run-list-scroll-wrap" @mouseenter="setScrollbarHover(runListScrollbar, true)" @mouseleave="setScrollbarHover(runListScrollbar, false)">
                <div ref="runListContainer" class="run-list" @scroll="updateRunListScrollbar">
                  <div
                    v-for="run in projectRuns"
                    :key="run.runId"
                    class="run-entry"
                  >
                    <button
                      class="run-select"
                      type="button"
                      :class="{ selected: selectedRun?.runId === run.runId }"
                      @click="selectedRunId = run.runId"
                    >
                      <i :class="run.state" />
                      <span><OverflowTooltip as="strong" :text="run.taskName">{{ run.taskName }}</OverflowTooltip><OverflowTooltip as="small" :text="stateLabel(run.state)">{{ stateLabel(run.state) }}</OverflowTooltip></span>
                      <time>{{ run.exitCode ?? run.pid ?? '—' }}</time>
                    </button>
                    <button
                      v-if="!isRunActive(run.state)"
                      class="run-dismiss"
                      type="button"
                      aria-label="移除运行记录"
                      title="移除运行记录"
                      :disabled="busyAction === `dismiss-${run.runId}`"
                      @click="handleDismissRun($event, run)"
                    >×</button>
                  </div>
                  <p v-if="!projectRuns.length">运行任务后会显示在这里</p>
                </div>
                <span v-if="runListScrollbar.overflow" class="scrollbar-rail light-scrollbar" :class="{ visible: runListScrollbar.hovered }">
                  <i
                    :style="{ height: `${runListScrollbar.thumbHeight}px`, transform: `translateY(${runListScrollbar.thumbTop}px)` }"
                    @pointerdown="startScrollbarDrag('runs', $event)"
                    @pointermove="dragScrollbar"
                    @pointerup="stopScrollbarDrag"
                    @pointercancel="stopScrollbarDrag"
                  />
                </span>
              </div>
            </aside>

            <section class="terminal-panel">
              <header class="terminal-toolbar">
                <div class="terminal-title">
                  <strong>{{ selectedRun?.taskName ?? '运行日志' }}</strong>
                  <b>{{ selectedRun ? stateLabel(selectedRun.state) : '等待任务' }}</b>
                </div>
                <div v-if="selectedRun" class="log-controls">
                  <div class="filter-tabs">
                    <button v-for="filter in ['all', 'stdout', 'stderr'] as const" :key="filter" type="button" :class="{ active: logFilter === filter }" @click="logFilter = filter">{{ filter === 'all' ? '全部' : filter }}</button>
                  </div>
                  <label class="auto-scroll"><input v-model="autoScroll" type="checkbox" /> 自动滚动</label>
                  <button v-if="isRunActive(selectedRun.state)" class="clear-button" type="button" @click="confirmStopRun($event, selectedRun!)">停止</button>
                  <button class="clear-button" type="button" @click="clearLogs(selectedRun.runId)">清空</button>
                </div>
              </header>

              <div class="scroll-fade-wrap terminal-scroll-wrap" @mouseenter="setScrollbarHover(logScrollbar, true)" @mouseleave="setScrollbarHover(logScrollbar, false)">
                <div ref="logContainer" class="terminal-output" @scroll="updateLogScrollbar">
                  <div v-if="visibleLogs.length" class="log-lines">
                    <div v-for="entry in visibleLogs" :key="entry.id" v-memo="[entry.id, logLinkAction]" class="log-line" :class="entry.stream">
                      <time>{{ formatTime(entry.timestamp) }}</time><b>{{ entry.stream }}</b>
                      <pre><template v-for="(segment, index) in splitLogMessage(entry.message)" :key="index"><button v-if="segment.url" class="log-link" type="button" :title="logLinkAction === 'open' ? '使用默认浏览器打开' : '复制链接'" @click="handleLogLink(segment.url)">{{ segment.text }}</button><span v-else>{{ segment.text }}</span></template></pre>
                    </div>
                  </div>
                  <div v-else class="terminal-empty"><p>{{ selectedRun ? '等待任务输出' : '从上方选择一个任务开始' }}</p><small>{{ selectedRun ? '标准输出和错误输出会显示在这里' : '常驻服务和一次性任务可同时运行' }}</small></div>
                </div>
                <span v-if="logScrollbar.overflow" class="scrollbar-rail terminal-scrollbar" :class="{ visible: logScrollbar.hovered }">
                  <i
                    :style="{ height: `${logScrollbar.thumbHeight}px`, transform: `translateY(${logScrollbar.thumbTop}px)` }"
                    @pointerdown="startScrollbarDrag('logs', $event)"
                    @pointermove="dragScrollbar"
                    @pointerup="stopScrollbarDrag"
                    @pointercancel="stopScrollbarDrag"
                  />
                </span>
              </div>
            </section>
          </section>
        </div>
      </template>

      <section v-else class="workspace-empty">
        <div class="empty-copy">
          <span class="empty-eyebrow"><i aria-hidden="true" /> 工作区 / 00</span>
          <h1>让第一个项目<br /><em>开始运行。</em></h1>
          <p>从一个本地目录开始，把启动命令、运行状态和日志收进同一个工作台。</p>
          <button class="empty-add-button" type="button" @click="openCreateForm">
            <span aria-hidden="true">+</span>
            添加本地项目
          </button>
        </div>

        <div class="empty-console" aria-hidden="true">
          <div class="empty-console-bar">
            <span class="console-dots"><i /><i /><i /></span>
            <b>workspace.queue</b>
            <span>READY</span>
          </div>
          <div class="empty-command"><span>$</span> runvoke add <i /></div>
          <div class="empty-queue">
            <div><span>01</span><OverflowTooltip as="b" text="选择项目目录">选择项目目录</OverflowTooltip><i>待命</i></div>
            <div><span>02</span><OverflowTooltip as="b" text="设置启动命令">设置启动命令</OverflowTooltip><i>待命</i></div>
            <div><span>03</span><OverflowTooltip as="b" text="进入后台运行">进入后台运行</OverflowTooltip><i>待命</i></div>
          </div>
          <div class="empty-console-footer"><span>QUEUE</span><b>0 / 3</b></div>
        </div>
      </section>
        </main>
      </template>
    </ResizableSplitPane>

    <ProjectForm
      :open="formOpen"
      :project="editingProject"
      :groups="projectGroups"
      @close="formOpen = false"
      @save="handleSave"
    />

    <ImportPromptDialog
      :open="importPromptOpen"
      :version="appVersion"
      @close="importPromptOpen = false"
      @import="openProjectImportDialog"
    />

    <ProjectImportDialog
      :open="projectImportDialogOpen"
      :candidates="projectImportCandidates"
      :loading="projectImportLoading"
      :importing="projectImporting"
      :source="projectImportSource"
      @close="projectImportDialogOpen = false"
      @reload="loadProjectImportCandidates"
      @import="importProjects"
    />

    <AgentProjectImportDialog
      :open="agentProjectImportDialogOpen"
      :candidates="agentProjectImportCandidates"
      :importing="agentProjectImporting"
      @close="agentProjectImportDialogOpen = false"
      @import="importAgentProjects"
    />

    <Teleport to="body">
      <Transition name="project-context-menu">
        <div v-if="projectContextMenu" class="project-context-layer" @keydown.esc="closeProjectContextMenu" @mousedown.self="closeProjectContextMenu" @contextmenu.self.prevent="closeProjectContextMenu">
          <section
            class="project-context-menu"
            role="menu"
            aria-label="项目操作"
            :style="{ top: `${projectContextMenu.top}px`, left: `${projectContextMenu.left}px` }"
          >
            <button ref="contextMenuEditButton" type="button" role="menuitem" @click="editProjectFromContextMenu">编辑</button>
            <div
              class="project-context-submenu"
              :class="{ 'is-open': projectContextSubmenuOpen }"
              @mouseenter="openProjectContextSubmenu"
              @mouseleave="scheduleProjectContextSubmenuClose"
              @focusin="openProjectContextSubmenu"
            >
              <button
                class="project-context-submenu-trigger"
                type="button"
                role="menuitem"
                aria-haspopup="menu"
                :aria-expanded="projectContextSubmenuOpen"
                @click="toggleProjectContextSubmenu"
              >
                <span>分组</span>
                <i aria-hidden="true">›</i>
              </button>
              <section
                class="project-context-submenu-panel"
                role="menu"
                aria-label="移动到分组"
                :style="{ top: `${projectContextMenu.submenuTop}px`, left: `${projectContextMenu.submenuLeft}px` }"
              >
                <button type="button" role="menuitem" @click="createGroupFromContextMenu">新建组</button>
                <span class="project-context-separator" />
                <button
                  v-for="group in projectGroups"
                  :key="group.id"
                  class="project-context-group-option"
                  type="button"
                  role="menuitemradio"
                  :aria-checked="projectContextMenu.project.groupId === group.id"
                  @click="moveContextProjectToGroup(group.id)"
                >
                  <i :class="{ current: projectContextMenu.project.groupId === group.id }" />
                  <OverflowTooltip :text="group.name">{{ group.name }}</OverflowTooltip>
                </button>
                <button
                  class="project-context-group-option"
                  type="button"
                  role="menuitemradio"
                  :aria-checked="!projectContextMenu.project.groupId"
                  @click="moveContextProjectToGroup(null)"
                >
                  <i :class="{ current: !projectContextMenu.project.groupId }" />
                  <span>未分组</span>
                </button>
              </section>
            </div>
          </section>
        </div>
      </Transition>
    </Teleport>

    <Teleport to="body">
      <Transition name="confirmation">
        <div v-if="pendingConfirmation" class="confirmation-layer" @keydown.esc="pendingConfirmation = null" @mousedown.self="pendingConfirmation = null">
          <section
            class="confirmation-popover"
            role="alertdialog"
            aria-modal="true"
            aria-labelledby="confirmation-message"
            :style="{ top: `${pendingConfirmation.top}px`, left: `${pendingConfirmation.left}px`, width: `${pendingConfirmation.width}px` }"
          >
            <p id="confirmation-message">{{ pendingConfirmation.message }}</p>
            <div class="confirmation-actions">
              <button ref="confirmationCancelButton" type="button" @click="pendingConfirmation = null">取消</button>
              <button
                :class="pendingConfirmation.variant === 'danger' ? 'confirmation-danger' : 'confirmation-primary'"
                type="button"
                @click="executePendingConfirmation"
              >{{ pendingConfirmation.confirmLabel }}</button>
            </div>
          </section>
        </div>
      </Transition>
    </Teleport>

    <Transition name="toast">
      <div v-if="toast" class="toast" :class="toast.type">
        {{ toast.message }}
      </div>
    </Transition>

    <div v-if="loading" class="loading-screen">
      <img class="loading-mark" :src="brandIcon" alt="" aria-hidden="true" />
      <span>正在加载项目…</span>
    </div>

    <div v-else-if="error" class="boot-error">
      <b>初始化失败</b>
      <span>{{ error }}</span>
    </div>
      </div>
  </TooltipProvider>
</template>
