<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, useTemplateRef } from 'vue'
import CursorIcon from './CursorIcon.vue'
import ResizableSplitPane from './ResizableSplitPane.vue'
import vscodeIcon from '../assets/vscode.svg'
import type { McpServerStatus, ProjectImportSource } from '../types'

type Theme = 'light' | 'dark'
type LogLinkAction = 'open' | 'copy'
type SettingsSection = 'general' | 'behavior' | 'updates' | 'project-config' | 'mcp'

defineProps<{
  autostartEnabled: boolean
  autostartBusy: boolean
  theme: Theme
  logLinkAction: LogLinkAction
  githubLinkVisible: boolean
  appVersion: string
  availableUpdateVersion: string
  availableUpdateBody: string
  updateChecking: boolean
  updateInstalling: boolean
  updateProgressLabel: string
  projectConfigOpening: boolean
  projectImportSource: ProjectImportSource
  projectImportBusy: boolean
  mcpStatus: McpServerStatus | null
  mcpBusy: boolean
  mcpConfigText: string
}>()

const emit = defineEmits<{
  close: []
  toggleAutostart: []
  setTheme: [theme: Theme]
  setLogLinkAction: [action: LogLinkAction]
  setGithubLinkVisible: [visible: boolean]
  checkUpdate: []
  installUpdate: [event: MouseEvent]
  openProjectConfigDirectory: []
  setProjectImportSource: [source: ProjectImportSource]
  openProjectImport: []
  setMcpEnabled: [enabled: boolean]
  copyMcpConfig: []
}>()

const activeSection = ref<SettingsSection>('general')
const projectImportMenuOpen = ref(false)
const projectImportSelect = useTemplateRef<HTMLElement>('projectImportSelect')
const projectImportTrigger = useTemplateRef<HTMLButtonElement>('projectImportTrigger')
const projectImportMenu = useTemplateRef<HTMLElement>('projectImportMenu')
const projectImportMenuPosition = ref({ top: 0, left: 0, width: 208 })

function navigateTo(section: SettingsSection) {
  activeSection.value = section
}

function selectProjectImportSource(source: ProjectImportSource) {
  projectImportMenuOpen.value = false
  emit('setProjectImportSource', source)
}

function updateProjectImportMenuPosition() {
  const rect = projectImportTrigger.value?.getBoundingClientRect()
  if (!rect)
    return
  projectImportMenuPosition.value = {
    top: rect.bottom + 6,
    left: rect.left,
    width: rect.width,
  }
}

function openProjectImportMenu() {
  updateProjectImportMenuPosition()
  projectImportMenuOpen.value = true
}

function toggleProjectImportMenu() {
  if (projectImportMenuOpen.value) {
    projectImportMenuOpen.value = false
    return
  }
  openProjectImportMenu()
}

function handleProjectImportTriggerKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') {
    projectImportMenuOpen.value = false
    return
  }
  if (['Enter', ' ', 'ArrowDown'].includes(event.key)) {
    event.preventDefault()
    openProjectImportMenu()
  }
}

function closeProjectImportMenuFromOutside(event: PointerEvent) {
  const target = event.target
  if (target instanceof Node && !projectImportSelect.value?.contains(target) && !projectImportMenu.value?.contains(target))
    projectImportMenuOpen.value = false
}

onMounted(() => {
  document.addEventListener('pointerdown', closeProjectImportMenuFromOutside)
  window.addEventListener('resize', updateProjectImportMenuPosition)
  window.addEventListener('scroll', updateProjectImportMenuPosition, true)
})

onBeforeUnmount(() => {
  document.removeEventListener('pointerdown', closeProjectImportMenuFromOutside)
  window.removeEventListener('resize', updateProjectImportMenuPosition)
  window.removeEventListener('scroll', updateProjectImportMenuPosition, true)
})
</script>

<template>
  <section class="settings-page" aria-labelledby="settings-title">
    <header class="settings-page-header">
      <h1 id="settings-title">设置</h1>
      <button class="settings-back-button" type="button" @click="emit('close')">
        <i aria-hidden="true" />
        返回工作台
      </button>
    </header>

    <ResizableSplitPane
      class="settings-page-body"
      :initial-start-size="292"
      :min-start-size="220"
      :min-end-size="520"
      :max-start-size="420"
      storage-key="runvoke:settings-sidebar-width"
      label="调整设置侧栏宽度"
    >
      <template #start>
        <nav class="settings-navigation" aria-label="设置分类">
        <span>设置分类</span>
        <button type="button" :class="{ active: activeSection === 'general' }" :aria-current="activeSection === 'general' ? 'page' : undefined" @click="navigateTo('general')">
          <b>01</b>
          <span>常规</span>
        </button>
        <button type="button" :class="{ active: activeSection === 'behavior' }" :aria-current="activeSection === 'behavior' ? 'page' : undefined" @click="navigateTo('behavior')">
          <b>02</b>
          <span>运行与日志</span>
        </button>
        <button type="button" :class="{ active: activeSection === 'updates' }" :aria-current="activeSection === 'updates' ? 'page' : undefined" @click="navigateTo('updates')">
          <b>03</b>
          <span>应用更新</span>
        </button>
        <button type="button" :class="{ active: activeSection === 'project-config' }" :aria-current="activeSection === 'project-config' ? 'page' : undefined" @click="navigateTo('project-config')">
          <b>04</b>
          <span>项目配置</span>
        </button>
        <button type="button" :class="{ active: activeSection === 'mcp' }" :aria-current="activeSection === 'mcp' ? 'page' : undefined" @click="navigateTo('mcp')">
          <b>05</b>
          <span>本地 MCP</span>
        </button>
        <footer class="settings-navigation-footer">
          <span>Runvoke</span>
          <code>{{ appVersion ? `v${appVersion}` : '读取中' }}</code>
        </footer>
        </nav>
      </template>

      <template #end>
        <div class="settings-content-scroll">
          <div class="settings-content">
          <section v-if="activeSection === 'general'" class="settings-section">
          <div class="settings-list">
            <article class="settings-item">
              <div>
                <strong>随系统启动</strong>
                <p>登录系统后自动启动，并在系统托盘中保持运行。</p>
              </div>
              <button
                class="settings-switch"
                :class="{ active: autostartEnabled }"
                type="button"
                role="switch"
                :aria-checked="autostartEnabled"
                :aria-label="autostartEnabled ? '关闭随系统启动' : '开启随系统启动'"
                :disabled="autostartBusy"
                @click="emit('toggleAutostart')"
              ><i /></button>
            </article>

            <article class="settings-item">
              <div>
                <strong>界面主题</strong>
                <p>选择适合当前环境的显示模式，设置会保存在本机。</p>
              </div>
              <div class="settings-segmented" role="radiogroup" aria-label="界面主题">
                <button type="button" role="radio" :aria-checked="theme === 'light'" :class="{ active: theme === 'light' }" @click="emit('setTheme', 'light')">浅色</button>
                <button type="button" role="radio" :aria-checked="theme === 'dark'" :class="{ active: theme === 'dark' }" @click="emit('setTheme', 'dark')">深色</button>
              </div>
            </article>

            <article class="settings-item">
              <div>
                <strong>GitHub 仓库入口</strong>
                <p>在左下角显示 GitHub 图标，点击后使用默认浏览器打开 Runvoke 仓库。</p>
              </div>
              <button
                class="settings-switch"
                :class="{ active: githubLinkVisible }"
                type="button"
                role="switch"
                :aria-checked="githubLinkVisible"
                :aria-label="githubLinkVisible ? '隐藏 GitHub 仓库入口' : '显示 GitHub 仓库入口'"
                @click="emit('setGithubLinkVisible', !githubLinkVisible)"
              ><i /></button>
            </article>
          </div>
          </section>

          <section v-else-if="activeSection === 'behavior'" class="settings-section">
          <div class="settings-list">
            <article class="settings-item">
              <div>
                <strong>点击日志链接时</strong>
                <p>识别到 HTTP 或 HTTPS 地址后，选择直接打开或复制链接。</p>
              </div>
              <div class="settings-segmented" role="radiogroup" aria-label="点击日志链接时执行">
                <button type="button" role="radio" :aria-checked="logLinkAction === 'open'" :class="{ active: logLinkAction === 'open' }" @click="emit('setLogLinkAction', 'open')">浏览器打开</button>
                <button type="button" role="radio" :aria-checked="logLinkAction === 'copy'" :class="{ active: logLinkAction === 'copy' }" @click="emit('setLogLinkAction', 'copy')">复制链接</button>
              </div>
            </article>
          </div>
          </section>

          <section v-else-if="activeSection === 'updates'" class="settings-section settings-update-section">
          <div class="settings-list">
            <article class="settings-item settings-version-item">
              <div>
                <strong>当前版本</strong>
                <p>{{ availableUpdateVersion ? `发现可用版本 v${availableUpdateVersion}` : '检查并获取最新的功能与问题修复。' }}</p>
              </div>
              <code>{{ appVersion ? `v${appVersion}` : '读取中' }}</code>
            </article>

            <article class="settings-update-actions">
              <div>
                <strong>{{ availableUpdateVersion ? '新版本已准备好' : '保持应用为最新版本' }}</strong>
                <p v-if="availableUpdateVersion">{{ availableUpdateBody || '已准备好下载并安装最新版本。' }}</p>
                <p v-else>{{ updateChecking ? '正在连接更新服务…' : '你也可以随时手动检查更新。' }}</p>
                <small v-if="updateInstalling">{{ updateProgressLabel }}</small>
              </div>
              <div>
                <button class="settings-secondary-button" type="button" :disabled="updateChecking || updateInstalling" @click="emit('checkUpdate')">
                  {{ updateChecking ? '正在检查' : availableUpdateVersion ? '重新检查' : '检查更新' }}
                </button>
                <button v-if="availableUpdateVersion" class="settings-primary-button" type="button" :disabled="updateInstalling" @click="emit('installUpdate', $event)">
                  {{ updateInstalling ? '正在安装' : '下载并安装' }}
                </button>
              </div>
            </article>
          </div>
          </section>

          <section v-else-if="activeSection === 'project-config'" class="settings-section">
          <div class="settings-list">
            <article class="settings-item">
              <div>
                <strong>项目配置目录</strong>
                <p>打开本机保存项目、任务和分组配置的目录。配置中的环境变量值可能包含敏感信息，请谨慎处理。</p>
              </div>
              <button class="settings-secondary-button" type="button" :disabled="projectConfigOpening" @click="emit('openProjectConfigDirectory')">
                {{ projectConfigOpening ? '正在打开' : '打开配置目录' }}
              </button>
            </article>
            <article class="settings-item settings-import-item">
              <div>
                <strong>从其他软件导入</strong>
                <p>读取已安装软件的本机项目记录，选择后批量导入。导入不会自动执行项目命令。</p>
              </div>
              <div class="settings-import-control">
                <div ref="projectImportSelect" class="group-select settings-source-select" :class="{ open: projectImportMenuOpen }">
                  <button
                    ref="projectImportTrigger"
                    class="group-select-trigger"
                    type="button"
                    role="combobox"
                    aria-label="选择项目导入来源"
                    aria-controls="project-import-source-options"
                    :aria-expanded="projectImportMenuOpen"
                    :disabled="projectImportBusy"
                    @click="toggleProjectImportMenu"
                    @keydown="handleProjectImportTriggerKeydown"
                  >
                    <span class="settings-source-label">
                      <img v-if="projectImportSource === 'vscode'" :src="vscodeIcon" alt="" aria-hidden="true">
                      <CursorIcon v-else />
                      <span>{{ projectImportSource === 'vscode' ? 'Visual Studio Code' : 'Cursor' }}</span>
                    </span>
                    <i aria-hidden="true" />
                  </button>
                </div>
                <button class="settings-secondary-button" type="button" :disabled="projectImportBusy" @click="emit('openProjectImport')">
                  {{ projectImportBusy ? '正在读取' : '导入项目' }}
                </button>
              </div>
            </article>
          </div>
          </section>

          <section v-else class="settings-section settings-mcp-section">
          <div class="settings-list">
            <article class="settings-item settings-mcp-toggle">
              <div>
                <strong>本地 MCP 服务</strong>
                <p>仅监听本机 127.0.0.1，供本机 Agent 读取项目、日志并执行受控操作。导入和更新始终需要你在 Runvoke 窗口中确认。</p>
              </div>
              <button
                class="settings-switch"
                :class="{ active: mcpStatus?.enabled }"
                type="button"
                role="switch"
                :aria-checked="Boolean(mcpStatus?.enabled)"
                :aria-label="mcpStatus?.enabled ? '关闭本地 MCP 服务' : '开启本地 MCP 服务'"
                :disabled="mcpBusy || !mcpStatus"
                @click="emit('setMcpEnabled', !mcpStatus?.enabled)"
              ><i /></button>
            </article>
            <article v-if="mcpStatus?.enabled" class="settings-mcp-details">
              <div class="settings-mcp-status-row">
                <div>
                  <strong>{{ mcpStatus.running ? '服务运行中' : '服务未运行' }}</strong>
                  <p>关闭此开关会立即停止本地 MCP 端点。</p>
                </div>
                <span class="settings-mcp-status" :class="{ online: mcpStatus.running }"><i />{{ mcpStatus.running ? '已连接' : '未连接' }}</span>
              </div>
              <dl class="settings-mcp-meta">
                <div><dt>监听地址</dt><dd><code>127.0.0.1</code></dd></div>
                <div><dt>端口</dt><dd><code>{{ mcpStatus.port }}</code></dd></div>
                <div><dt>MCP 端点</dt><dd><code>{{ mcpStatus.endpoint }}</code></dd></div>
                <div><dt>认证令牌</dt><dd><code>{{ mcpStatus.authorizationToken }}</code></dd></div>
              </dl>
              <div class="settings-mcp-config">
                <div class="settings-mcp-config-heading">
                  <div><strong>客户端配置</strong><p>复制到支持 Streamable HTTP 的 Agent 配置中。</p></div>
                  <button class="settings-secondary-button" type="button" @click="emit('copyMcpConfig')">复制配置</button>
                </div>
                <pre>{{ mcpConfigText }}</pre>
              </div>
            </article>
            <article v-else class="settings-mcp-offline">
              <strong>MCP 服务当前关闭</strong>
              <p>开启后会生成本机认证令牌，并显示可复制的连接配置。</p>
              <dl v-if="mcpStatus" class="settings-mcp-meta">
                <div><dt>监听地址</dt><dd><code>127.0.0.1</code></dd></div>
                <div><dt>端口</dt><dd><code>{{ mcpStatus.port }}</code></dd></div>
                <div><dt>MCP 端点</dt><dd><code>{{ mcpStatus.endpoint }}</code></dd></div>
              </dl>
            </article>
          </div>
          </section>
          </div>
        </div>
      </template>
    </ResizableSplitPane>

    <Teleport to="body">
      <Transition name="group-select-menu">
        <div
          v-if="projectImportMenuOpen"
          id="project-import-source-options"
          ref="projectImportMenu"
          class="group-select-menu settings-source-menu"
          role="listbox"
          aria-label="项目导入来源"
          :style="{ top: `${projectImportMenuPosition.top}px`, left: `${projectImportMenuPosition.left}px`, width: `${projectImportMenuPosition.width}px` }"
        >
          <button class="group-select-option" type="button" role="option" :aria-selected="projectImportSource === 'vscode'" @click="selectProjectImportSource('vscode')">
            <i :class="{ selected: projectImportSource === 'vscode' }" />
            <span class="settings-source-label">
              <img :src="vscodeIcon" alt="" aria-hidden="true">
              <span>Visual Studio Code</span>
            </span>
          </button>
          <button class="group-select-option" type="button" role="option" :aria-selected="projectImportSource === 'cursor'" @click="selectProjectImportSource('cursor')">
            <i :class="{ selected: projectImportSource === 'cursor' }" />
            <span class="settings-source-label">
              <CursorIcon />
              <span>Cursor</span>
            </span>
          </button>
        </div>
      </Transition>
    </Teleport>
  </section>
</template>
