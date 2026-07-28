<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, useTemplateRef, watch } from 'vue'
import { isTauri } from '@tauri-apps/api/core'
import { relaunch } from '@tauri-apps/plugin-process'
import { check, type DownloadEvent, type Update } from '@tauri-apps/plugin-updater'
import brandIcon from '../src-tauri/icons/128x128.png'
import ProjectForm from './components/ProjectForm.vue'
import { useLauncher } from './composables/useLauncher'
import type { LogStream, ProjectConfig, ProjectTask, RuntimeStatus } from './types'

const {
  projects,
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
} = useLauncher()

const search = ref('')
const formOpen = ref(false)
const editingProject = ref<ProjectConfig | null>(null)
const settingsOpen = ref(false)
const saving = ref(false)
const busyAction = ref('')
const temporaryCommand = ref('')
const projectOpenMenuOpen = ref(false)
const logFilter = ref<'all' | LogStream>('all')
const autoScroll = ref(true)
const toast = ref<{ type: 'success' | 'error', message: string } | null>(null)
const availableUpdate = ref<Update | null>(null)
const updatePopoverOpen = ref(false)
const updateChecking = ref(false)
const updateInstalling = ref(false)
const updateProgress = ref({ received: 0, total: 0 })
const logContainer = useTemplateRef<HTMLDivElement>('logContainer')
const runListContainer = useTemplateRef<HTMLDivElement>('runListContainer')
const projectListContainer = useTemplateRef<HTMLElement>('projectListContainer')
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
  top: number
  left: number
  width: number
  action: () => Promise<void>
}

type ProjectContextMenu = {
  project: ProjectConfig
  top: number
  left: number
}

const pendingConfirmation = ref<PendingConfirmation | null>(null)
const projectContextMenu = ref<ProjectContextMenu | null>(null)
let toastTimer: ReturnType<typeof setTimeout> | undefined
let updateCheckTimer: ReturnType<typeof setInterval> | undefined

const filteredProjects = computed(() => {
  const keyword = search.value.trim().toLocaleLowerCase()
  if (!keyword)
    return projects.value
  return projects.value.filter((project) =>
    `${project.name} ${project.directory} ${project.command}`.toLocaleLowerCase().includes(keyword),
  )
})

const visibleLogs = computed(() => {
  if (logFilter.value === 'all')
    return selectedLogs.value
  return selectedLogs.value.filter((entry) => entry.stream === logFilter.value)
})

const runningCount = computed(() =>
  Object.values(runsById.value).filter((run) => run.state === 'running').length,
)
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
  () => filteredProjects.value.length,
  () => {
    void nextTick(updateProjectListScrollbar)
  },
)

onMounted(() => {
  window.addEventListener('resize', updateScrollbars)
  window.addEventListener('contextmenu', preventNativeContextMenu)
  void nextTick(updateScrollbars)
  void checkForUpdate()
  updateCheckTimer = window.setInterval(() => void checkForUpdate(), 4 * 60 * 60 * 1_000)
})

onBeforeUnmount(() => {
  window.removeEventListener('resize', updateScrollbars)
  window.removeEventListener('contextmenu', preventNativeContextMenu)
  if (updateCheckTimer)
    clearInterval(updateCheckTimer)
})

function preventNativeContextMenu(event: MouseEvent) {
  event.preventDefault()
}

function notify(type: 'success' | 'error', message: string) {
  toast.value = { type, message }
  if (toastTimer)
    clearTimeout(toastTimer)
  toastTimer = setTimeout(() => {
    toast.value = null
  }, 3_200)
}

async function checkForUpdate(showNoUpdate = false) {
  if (!isTauri() || updateInstalling.value)
    return

  updateChecking.value = true
  try {
    availableUpdate.value = await check()
    if (!availableUpdate.value && showNoUpdate)
      notify('success', '当前已是最新版本')
  }
  catch (value) {
    if (showNoUpdate)
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
  updateProgress.value = { received: 0, total: 0 }
  try {
    await update.downloadAndInstall(trackUpdateDownload)
    await relaunch()
  }
  catch (value) {
    updateInstalling.value = false
    notify('error', `更新安装失败：${String(value)}`)
  }
}

function openCreateForm() {
  editingProject.value = null
  formOpen.value = true
}

function openEditForm() {
  editingProject.value = selectedProject.value
  formOpen.value = true
}

function showProjectContextMenu(event: MouseEvent, project: ProjectConfig) {
  event.preventDefault()
  selectedId.value = project.id

  const target = event.currentTarget
  const rect = target instanceof HTMLElement ? target.getBoundingClientRect() : null
  const anchorX = event.clientX || rect?.left || 12
  const anchorY = event.clientY || rect?.bottom || 12
  const menuWidth = 128
  const menuHeight = 42
  const viewportPadding = 12
  projectContextMenu.value = {
    project,
    left: Math.min(anchorX, window.innerWidth - menuWidth - viewportPadding),
    top: Math.min(anchorY, window.innerHeight - menuHeight - viewportPadding),
  }
  void nextTick(() => contextMenuEditButton.value?.focus())
}

function closeProjectContextMenu() {
  projectContextMenu.value = null
}

function editProjectFromContextMenu() {
  const project = projectContextMenu.value?.project
  if (!project)
    return
  closeProjectContextMenu()
  editingProject.value = project
  formOpen.value = true
}

async function handleSave(project: ProjectConfig) {
  saving.value = true
  try {
    await saveProject(project)
    formOpen.value = false
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

async function handleDelete() {
  const project = selectedProject.value
  if (!project || !window.confirm(`确定删除“${project.name}”吗？运行中的进程也会被停止。`))
    return
  await perform('delete', () => removeProject(project.id), '项目已删除')
}

function updateScrollbar(container: HTMLElement | null, scrollbar: ScrollbarState) {
  if (!container)
    return

  const visibleHeight = container.clientHeight
  const scrollableHeight = container.scrollHeight
  scrollbar.overflow = scrollableHeight > visibleHeight + 1
  if (!scrollbar.overflow) {
    scrollbar.thumbHeight = 0
    scrollbar.thumbTop = 0
    return
  }

  scrollbar.thumbHeight = Math.min(visibleHeight, Math.max(28, Math.round(visibleHeight * visibleHeight / scrollableHeight)))
  const scrollRange = scrollableHeight - visibleHeight
  const trackRange = visibleHeight - scrollbar.thumbHeight
  scrollbar.thumbTop = trackRange ? Math.round(container.scrollTop * trackRange / scrollRange) : 0
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
) {
  const target = event.currentTarget
  if (!(target instanceof HTMLElement))
    return

  const rect = target.getBoundingClientRect()
  const viewportPadding = 12
  const popoverWidth = Math.min(280, window.innerWidth - viewportPadding * 2)
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

  pendingConfirmation.value = { message, confirmLabel, top, left, width: popoverWidth, action }
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

function projectState(projectId: string) {
  return Object.values(runsById.value).find((run) => run.projectId === projectId && isRunActive(run.state))?.state ?? 'stopped'
}

function projectStateLabel(projectId: string) {
  const state = projectState(projectId)
  if (state === 'starting')
    return '启动中'
  if (state === 'running')
    return '运行中'
  if (state === 'stopping')
    return '停止中'
  return ''
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
    () => perform(`stop-${run.runId}`, () => stopRun(run.runId), '任务已停止'),
  )
}

async function handleTaskAction(event: MouseEvent, task: ProjectTask) {
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
  if (!project || !command)
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
  <div class="app-frame" @contextmenu.prevent>
    <aside class="sidebar">
      <header class="brand-row">
        <img class="brand-mark" :src="brandIcon" alt="" aria-hidden="true" />
        <div>
          <strong>Runvoke</strong>
          <span>本地开发工作台</span>
        </div>
        <button class="settings-button" type="button" aria-label="设置" :aria-expanded="settingsOpen" @click="settingsOpen = !settingsOpen">设置</button>
      </header>

      <Transition name="settings">
        <section v-if="settingsOpen" class="settings-card">
          <div>
            <strong>随系统启动</strong>
            <span>登录后保持托盘驻留</span>
          </div>
          <button
            class="switch"
            :class="{ active: autostartEnabled }"
            type="button"
            role="switch"
            :aria-checked="autostartEnabled"
            :disabled="busyAction === 'autostart'"
            @click="toggleAutostart"
          ><i /></button>
        </section>
      </Transition>

      <div class="fleet-heading">
        <div>
          <span>我的项目</span>
          <b>{{ runningCount ? `${runningCount} 个任务运行中` : '当前没有运行任务' }} · 共 {{ projects.length }} 个项目</b>
        </div>
        <button class="add-button" type="button" aria-label="添加项目" title="添加项目" @click="openCreateForm">+</button>
      </div>

      <label class="search-box">
        <span aria-hidden="true">搜索</span>
        <input v-model="search" placeholder="搜索项目、目录或命令" />
      </label>

      <div class="scroll-fade-wrap project-list-scroll-wrap" @mouseenter="setScrollbarHover(projectListScrollbar, true)" @mouseleave="setScrollbarHover(projectListScrollbar, false)">
        <nav ref="projectListContainer" class="project-list" aria-label="项目列表" @scroll="updateProjectListScrollbar">
          <button
            v-for="project in filteredProjects"
            :key="project.id"
            class="project-item"
            :class="{ selected: selectedId === project.id, active: isRunActive(projectState(project.id)) }"
            type="button"
            @click="selectedId = project.id"
            @contextmenu="showProjectContextMenu($event, project)"
          >
            <span class="status-beacon" :class="projectState(project.id)"><i /></span>
            <span class="project-copy">
              <strong>{{ project.name }}</strong>
              <small>{{ shortPath(project.directory) }}</small>
            </span>
            <span class="project-meta">
              <span v-if="isRunActive(projectState(project.id))" class="project-state" :class="projectState(project.id)">{{ projectStateLabel(project.id) }}</span>
              <span v-if="project.port" class="port-tag">:{{ project.port }}</span>
            </span>
          </button>

          <div v-if="!loading && !filteredProjects.length" class="list-empty">
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
        <span>应用将在系统托盘中保持运行</span>
        <button
          v-if="availableUpdate"
          class="update-trigger"
          type="button"
          :aria-expanded="updatePopoverOpen"
          :disabled="updateInstalling"
          @click="updatePopoverOpen = !updatePopoverOpen"
        ><i />更新</button>
        <Transition name="update-popover">
          <section v-if="updatePopoverOpen && availableUpdate" class="update-popover" @keydown.esc="updatePopoverOpen = false">
            <span>发现新版本</span>
            <strong>v{{ availableUpdate.version }}</strong>
            <p>{{ availableUpdate.body || '已准备好下载并安装最新版本。' }}</p>
            <small v-if="updateInstalling">{{ updateProgressLabel() }}</small>
            <div>
              <button type="button" :disabled="updateInstalling || updateChecking" @click="checkForUpdate(true)">重新检查</button>
              <button class="update-install-button" type="button" :disabled="updateInstalling" @click="installAvailableUpdate">{{ updateInstalling ? '正在安装' : '下载并安装' }}</button>
            </div>
          </section>
        </Transition>
      </footer>
    </aside>

    <main class="workspace">
      <template v-if="selectedProject">
        <header class="workspace-header">
          <div class="project-title">
            <span class="section-kicker">当前项目</span>
            <h1>{{ selectedProject.name }}</h1>
            <code>{{ selectedProject.directory }}</code>
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
            <button class="danger-link" type="button" @click="handleDelete">删除</button>
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
                :disabled="busyAction === `task-${task.id}` || Boolean(activeRunForTask(task.id) && busyAction === `stop-${activeRunForTask(task.id)!.runId}`)"
                @click="handleTaskAction($event, task)"
              >
                <span class="task-kind">{{ taskModeLabel(task.mode) }}</span>
                <span class="task-copy">
                  <strong>{{ task.name }}</strong>
                  <code>{{ task.command }}</code>
                  <small v-if="activeRunForTask(task.id)" class="task-running"><i />{{ stateLabel(activeRunForTask(task.id)?.state) }}</small>
                </span>
                <span class="task-action" aria-hidden="true" />
              </button>
            </div>
            <form class="temporary-command" @submit.prevent="handleTemporaryCommand">
              <span>$</span>
              <input v-model="temporaryCommand" aria-label="临时命令" placeholder="输入一次性临时命令，例如 pnpm build" />
              <button type="submit" :disabled="!temporaryCommand.trim() || busyAction === 'temporary'">执行</button>
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
                      <span><strong>{{ run.taskName }}</strong><small>{{ stateLabel(run.state) }}</small></span>
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
                    <div v-for="entry in visibleLogs" :key="entry.id" class="log-line" :class="entry.stream"><time>{{ formatTime(entry.timestamp) }}</time><b>{{ entry.stream }}</b><pre>{{ entry.message || ' ' }}</pre></div>
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
            <div><span>01</span><b>选择项目目录</b><i>待命</i></div>
            <div><span>02</span><b>设置启动命令</b><i>待命</i></div>
            <div><span>03</span><b>进入后台运行</b><i>待命</i></div>
          </div>
          <div class="empty-console-footer"><span>QUEUE</span><b>0 / 3</b></div>
        </div>
      </section>
    </main>

    <ProjectForm
      :open="formOpen"
      :project="editingProject"
      @close="formOpen = false"
      @save="handleSave"
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
              <button class="confirmation-danger" type="button" @click="executePendingConfirmation">{{ pendingConfirmation.confirmLabel }}</button>
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
</template>
