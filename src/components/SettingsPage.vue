<script setup lang="ts">
import { ref } from 'vue'
import ResizableSplitPane from './ResizableSplitPane.vue'

type Theme = 'light' | 'dark'
type LogLinkAction = 'open' | 'copy'
type SettingsSection = 'general' | 'behavior' | 'updates' | 'project-config'

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
}>()

const activeSection = ref<SettingsSection>('general')

function navigateTo(section: SettingsSection) {
  activeSection.value = section
}
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

          <section v-else class="settings-section">
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
          </div>
          </section>
          </div>
        </div>
      </template>
    </ResizableSplitPane>
  </section>
</template>
