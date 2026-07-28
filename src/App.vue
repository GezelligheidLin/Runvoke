<script setup lang="ts">
import { computed, nextTick, ref, useTemplateRef, watch } from 'vue'
import brandIcon from '../src-tauri/icons/128x128.png'
import ProjectForm from './components/ProjectForm.vue'
import { useLauncher } from './composables/useLauncher'
import type { LogStream, ProjectConfig } from './types'

const {
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
} = useLauncher()

const search = ref('')
const formOpen = ref(false)
const editingProject = ref<ProjectConfig | null>(null)
const settingsOpen = ref(false)
const saving = ref(false)
const busyAction = ref('')
const logFilter = ref<'all' | LogStream>('all')
const autoScroll = ref(true)
const toast = ref<{ type: 'success' | 'error', message: string } | null>(null)
const logContainer = useTemplateRef<HTMLDivElement>('logContainer')
let toastTimer: ReturnType<typeof setTimeout> | undefined

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

const isRunning = computed(() => selectedRuntime.value?.state === 'running')
const isTransitioning = computed(() => ['starting', 'stopping'].includes(selectedRuntime.value?.state ?? ''))
const runningCount = computed(() =>
  projects.value.filter((project) => runtimeById.value[project.id]?.state === 'running').length,
)

watch(
  () => visibleLogs.value.length,
  async () => {
    if (!autoScroll.value)
      return
    await nextTick()
    if (logContainer.value)
      logContainer.value.scrollTop = logContainer.value.scrollHeight
  },
)

function notify(type: 'success' | 'error', message: string) {
  toast.value = { type, message }
  if (toastTimer)
    clearTimeout(toastTimer)
  toastTimer = setTimeout(() => {
    toast.value = null
  }, 3_200)
}

function openCreateForm() {
  editingProject.value = null
  formOpen.value = true
}

function openEditForm() {
  editingProject.value = selectedProject.value
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
  }[state ?? 'stopped'] ?? '已停止'
}
</script>

<template>
  <div class="app-frame">
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
          <b>{{ runningCount }} 个正在运行，共 {{ projects.length }} 个</b>
        </div>
        <button class="add-button" type="button" aria-label="添加项目" title="添加项目" @click="openCreateForm">+</button>
      </div>

      <label class="search-box">
        <span aria-hidden="true">搜索</span>
        <input v-model="search" placeholder="搜索项目、目录或命令" />
      </label>

      <nav class="project-list" aria-label="项目列表">
        <button
          v-for="project in filteredProjects"
          :key="project.id"
          class="project-item"
          :class="{ selected: selectedId === project.id }"
          type="button"
          @click="selectedId = project.id"
        >
          <span class="status-beacon" :class="runtimeById[project.id]?.state ?? 'stopped'"><i /></span>
          <span class="project-copy">
            <strong>{{ project.name }}</strong>
            <small>{{ shortPath(project.directory) }}</small>
          </span>
          <span v-if="project.port" class="port-tag">:{{ project.port }}</span>
        </button>

        <div v-if="!loading && !filteredProjects.length" class="list-empty">
          <p>{{ projects.length ? '没有匹配的项目' : '还没有接入项目' }}</p>
          <button v-if="!projects.length" type="button" @click="openCreateForm">添加第一个项目</button>
        </div>
      </nav>

      <footer class="sidebar-footer">
        <span class="tray-dot" />
        应用将在系统托盘中保持运行
      </footer>
    </aside>

    <main class="workspace">
      <template v-if="selectedProject && selectedRuntime">
        <header class="workspace-header">
          <div class="project-title">
            <span class="section-kicker">当前项目</span>
            <h1>{{ selectedProject.name }}</h1>
            <code>{{ selectedProject.command }}</code>
          </div>
          <div class="header-actions">
            <button type="button" @click="perform('code', () => openInVscode(selectedProject!.directory), '已交给 VS Code 打开')">用 VS Code 打开</button>
            <button type="button" @click="openEditForm">编辑</button>
            <button class="danger-link" type="button" @click="handleDelete">删除</button>
          </div>
        </header>

        <section class="control-deck">
          <div class="primary-controls">
            <button
              v-if="!isRunning"
              class="launch-button"
              type="button"
              :disabled="isTransitioning"
              @click="perform('start', () => runProject(selectedProject!.id), '项目已在后台启动')"
            >
              <span class="play-icon">▶</span>
              {{ isTransitioning ? stateLabel(selectedRuntime.state) : '启动项目' }}
            </button>
            <button
              v-else
              class="stop-button"
              type="button"
              :disabled="isTransitioning"
              @click="perform('stop', () => stopProject(selectedProject!.id), '项目及其子进程已停止')"
            >
              <span>■</span> 停止项目
            </button>
            <button
              class="restart-button"
              type="button"
              :disabled="!isRunning || isTransitioning"
              @click="perform('restart', () => restartProject(selectedProject!.id), '项目已重新启动')"
            >重启</button>
          </div>

          <div class="metrics-strip">
            <article>
              <span>运行状态</span>
              <strong class="metric-status" :class="selectedRuntime.state">
                <i /> {{ stateLabel(selectedRuntime.state) }}
              </strong>
            </article>
            <article>
              <span>进程 ID</span>
              <strong>{{ selectedRuntime.pid ?? '—' }}</strong>
            </article>
            <article>
              <span>端口</span>
              <strong>{{ selectedProject.port ? `:${selectedProject.port}` : '自动' }}</strong>
            </article>
            <article>
              <span>运行时长</span>
              <strong>{{ formatUptime(selectedRuntime.startedAt) }}</strong>
            </article>
          </div>
        </section>

        <section class="terminal-panel">
          <header class="terminal-toolbar">
            <div class="terminal-title">
              <strong>运行日志</strong>
              <b>{{ selectedLogs.length.toLocaleString() }} 条</b>
            </div>
            <div class="log-controls">
              <div class="filter-tabs">
                <button
                  v-for="filter in ['all', 'stdout', 'stderr'] as const"
                  :key="filter"
                  type="button"
                  :class="{ active: logFilter === filter }"
                  @click="logFilter = filter"
                >{{ filter === 'all' ? '全部' : filter }}</button>
              </div>
              <label class="auto-scroll">
                <input v-model="autoScroll" type="checkbox" />
                自动滚动
              </label>
              <button class="clear-button" type="button" @click="clearLogs(selectedProject!.id)">清空</button>
            </div>
          </header>

          <div ref="logContainer" class="terminal-output">
            <div v-if="visibleLogs.length" class="log-lines">
              <div v-for="entry in visibleLogs" :key="entry.id" class="log-line" :class="entry.stream">
                <time>{{ formatTime(entry.timestamp) }}</time>
                <b>{{ entry.stream }}</b>
                <pre>{{ entry.message || ' ' }}</pre>
              </div>
            </div>
            <div v-else class="terminal-empty">
              <p>等待进程输出</p>
              <small>启动项目后，标准输出和错误输出会显示在这里</small>
            </div>
          </div>
        </section>
      </template>

      <section v-else class="workspace-empty">
        <span class="section-kicker">项目工作台</span>
        <h1>暂无项目</h1>
        <p>添加一个本地项目后，可以在这里统一管理它的后台运行状态和日志。</p>
        <button class="launch-button" type="button" @click="openCreateForm">添加项目</button>
      </section>
    </main>

    <ProjectForm
      :open="formOpen"
      :project="editingProject"
      @close="formOpen = false"
      @save="handleSave"
    />

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
