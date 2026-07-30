<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import CursorIcon from './CursorIcon.vue'
import OverflowTooltip from './OverflowTooltip.vue'
import vscodeIcon from '../assets/vscode.svg'
import type { ImportedProject, ProjectImportSource } from '../types'

const props = defineProps<{
  open: boolean
  candidates: ImportedProject[]
  loading: boolean
  importing: boolean
  source: ProjectImportSource
}>()

const emit = defineEmits<{
  close: []
  reload: []
  import: [projects: ImportedProject[]]
}>()

const selectedDirectories = ref<string[]>([])
const directoryNameDirectories = ref<string[]>([])

watch(() => [props.open, props.candidates] as const, ([open]) => {
  if (open) {
    selectedDirectories.value = props.candidates.map(project => project.directory)
    directoryNameDirectories.value = []
  }
}, { deep: true, immediate: true })

const selectedProjects = computed(() => props.candidates
  .filter(project => selectedDirectories.value.includes(project.directory))
  .map(project => directoryNameDirectories.value.includes(project.directory)
    ? { ...project, name: directoryName(project.directory) }
    : project))
const allProjectsSelected = computed(() => props.candidates.length > 0 && selectedProjects.value.length === props.candidates.length)
const partiallySelected = computed(() => selectedProjects.value.length > 0 && !allProjectsSelected.value)
const allDirectoryNamesSelected = computed(() => props.candidates.length > 0 && directoryNameDirectories.value.length === props.candidates.length)
const partiallyDirectoryNamesSelected = computed(() => directoryNameDirectories.value.length > 0 && !allDirectoryNamesSelected.value)
const sourceLabel = computed(() => props.source === 'cursor' ? 'Cursor' : 'Visual Studio Code')
const sourceShortLabel = computed(() => props.source === 'cursor' ? 'CURSOR' : 'VS CODE')

function toggleProject(directory: string) {
  selectedDirectories.value = selectedDirectories.value.includes(directory)
    ? selectedDirectories.value.filter(value => value !== directory)
    : [...selectedDirectories.value, directory]
}

function toggleAllProjects() {
  selectedDirectories.value = allProjectsSelected.value
    ? []
    : props.candidates.map(project => project.directory)
}

function directoryName(directory: string) {
  return directory.replace(/[\\/]+$/, '').split(/[\\/]/).filter(Boolean).at(-1) || directory
}

function toggleDirectoryName(directory: string) {
  directoryNameDirectories.value = directoryNameDirectories.value.includes(directory)
    ? directoryNameDirectories.value.filter(value => value !== directory)
    : [...directoryNameDirectories.value, directory]
}

function toggleAllDirectoryNames() {
  directoryNameDirectories.value = allDirectoryNamesSelected.value
    ? []
    : props.candidates.map(project => project.directory)
}
</script>

<template>
  <Teleport to="body">
    <Transition name="modal">
      <div v-if="open" class="modal-layer import-modal-layer" @mousedown.self="emit('close')" @keydown.esc="emit('close')">
        <section class="import-dialog" role="dialog" aria-modal="true" aria-labelledby="project-import-title">
          <header class="import-dialog-header">
            <div>
              <span class="section-kicker">项目导入 / {{ sourceShortLabel }}</span>
              <h2 id="project-import-title">导入项目</h2>
              <p>从 {{ sourceLabel }} 最近打开的目录中选择要加入 Runvoke 的项目。</p>
            </div>
            <button class="icon-button" type="button" aria-label="关闭" title="关闭" @click="emit('close')">×</button>
          </header>

          <div class="import-dialog-body">
            <div class="import-dialog-toolbar">
              <div class="import-dialog-toolbar-leading">
                <span>{{ loading ? `正在读取 ${sourceLabel} 项目记录…` : `找到 ${candidates.length} 个可导入项目` }}</span>
                <label v-if="!loading && candidates.length" class="import-select-all">
                  <input
                    type="checkbox"
                    :checked="allProjectsSelected"
                    :indeterminate="partiallySelected"
                    aria-label="全选可导入项目"
                    @change="toggleAllProjects"
                  >
                  <span>全选</span>
                </label>
                <label v-if="!loading && candidates.length" class="import-select-all import-directory-name-all">
                  <input
                    type="checkbox"
                    :checked="allDirectoryNamesSelected"
                    :indeterminate="partiallyDirectoryNamesSelected"
                    aria-label="全部使用项目目录名称"
                    @change="toggleAllDirectoryNames"
                  >
                  <span>全部使用目录名</span>
                </label>
              </div>
              <button type="button" :disabled="loading || importing" @click="emit('reload')">重新读取</button>
            </div>

            <div v-if="loading" class="import-dialog-empty">正在读取本机 {{ sourceLabel }} 工作区记录</div>
            <div v-else-if="!candidates.length" class="import-dialog-empty">
              <strong>没有发现可导入项目</strong>
              <p>请先在 {{ sourceLabel }} 中打开项目目录，或确认项目已经被 Runvoke 导入。</p>
            </div>
            <div v-else class="import-project-list">
              <div v-for="project in candidates" :key="project.directory" class="import-project-option" :class="{ selected: selectedDirectories.includes(project.directory) }">
                <label class="import-project-selection">
                  <input type="checkbox" :checked="selectedDirectories.includes(project.directory)" :aria-label="`选择 ${project.name}`" @change="toggleProject(project.directory)" />
                </label>
                <span class="import-project-mark" :class="{ cursor: source === 'cursor' }" aria-hidden="true">
                  <img v-if="source === 'vscode'" :src="vscodeIcon" alt="">
                  <CursorIcon v-else />
                </span>
                <span class="import-project-copy">
                  <strong><OverflowTooltip :text="directoryNameDirectories.includes(project.directory) ? directoryName(project.directory) : project.name">{{ directoryNameDirectories.includes(project.directory) ? directoryName(project.directory) : project.name }}</OverflowTooltip></strong>
                  <small><OverflowTooltip :text="project.directory">{{ project.directory }}</OverflowTooltip></small>
                  <label class="import-directory-name-toggle">
                    <input type="checkbox" :checked="directoryNameDirectories.includes(project.directory)" :aria-label="`对 ${project.name} 使用项目目录名称`" @change="toggleDirectoryName(project.directory)" />
                    <span>使用目录名</span>
                  </label>
                </span>
                <code>{{ project.suggestedCommand || '需配置命令' }}</code>
              </div>
            </div>
          </div>

          <footer class="import-dialog-footer">
            <span>{{ selectedProjects.length }} 个项目已选中</span>
            <div>
              <button class="button-ghost" type="button" :disabled="importing" @click="emit('close')">取消</button>
              <button class="button-primary" type="button" :disabled="!selectedProjects.length || loading || importing" @click="emit('import', selectedProjects)">
                {{ importing ? '正在导入…' : '导入' }}
              </button>
            </div>
          </footer>
        </section>
      </div>
    </Transition>
  </Teleport>
</template>
