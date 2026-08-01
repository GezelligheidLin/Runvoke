<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import OverflowTooltip from './OverflowTooltip.vue'
import type { ImportedProject } from '../types'

const props = defineProps<{
  open: boolean
  candidates: ImportedProject[]
  importing: boolean
}>()

const emit = defineEmits<{
  close: []
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
      <div v-if="open" class="modal-layer agent-import-modal-layer" @mousedown.self="emit('close')" @keydown.esc="emit('close')">
        <section class="agent-import-dialog" role="dialog" aria-modal="true" aria-labelledby="agent-project-import-title">
          <header class="agent-import-dialog-header">
            <div>
              <span class="section-kicker"><i aria-hidden="true" /> AGENT 请求</span>
              <h2 id="agent-project-import-title">纳入本地项目</h2>
              <p>本机 Agent 提交了一批候选工作目录。请自行筛选，确认后才会保存到 Runvoke；Runvoke 不会读取或混入其他软件的项目记录。</p>
            </div>
            <button class="icon-button" type="button" aria-label="拒绝本次请求" title="拒绝本次请求" @click="emit('close')">×</button>
          </header>

          <div class="import-dialog-body">
            <div class="import-dialog-toolbar">
              <div class="import-dialog-toolbar-leading">
                <span>Agent 提交了 {{ candidates.length }} 个候选项目</span>
                <label v-if="candidates.length" class="import-select-all">
                  <input
                    type="checkbox"
                    :checked="allProjectsSelected"
                    :indeterminate="partiallySelected"
                    aria-label="全选 Agent 候选项目"
                    @change="toggleAllProjects"
                  >
                  <span>全选</span>
                </label>
                <label v-if="candidates.length" class="import-select-all import-directory-name-all">
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
            </div>

            <div v-if="!candidates.length" class="import-dialog-empty">
              <strong>没有可供确认的项目</strong>
              <p>这些项目可能已经在 Runvoke 中，或 Agent 提供的目录已不可用。</p>
            </div>
            <div v-else class="import-project-list">
              <div v-for="project in candidates" :key="project.directory" class="import-project-option" :class="{ selected: selectedDirectories.includes(project.directory) }">
                <label class="import-project-selection">
                  <input type="checkbox" :checked="selectedDirectories.includes(project.directory)" :aria-label="`选择 ${project.name}`" @change="toggleProject(project.directory)">
                </label>
                <span class="agent-import-project-mark" aria-hidden="true">AG</span>
                <span class="import-project-copy">
                  <strong><OverflowTooltip :text="directoryNameDirectories.includes(project.directory) ? directoryName(project.directory) : project.name">{{ directoryNameDirectories.includes(project.directory) ? directoryName(project.directory) : project.name }}</OverflowTooltip></strong>
                  <small><OverflowTooltip :text="project.directory">{{ project.directory }}</OverflowTooltip></small>
                  <label class="import-directory-name-toggle">
                    <input type="checkbox" :checked="directoryNameDirectories.includes(project.directory)" :aria-label="`对 ${project.name} 使用项目目录名称`" @change="toggleDirectoryName(project.directory)">
                    <span>使用目录名</span>
                  </label>
                </span>
                <code>{{ project.suggestedCommand || '需配置命令' }}</code>
              </div>
            </div>
          </div>

          <footer class="import-dialog-footer agent-import-dialog-footer">
            <span>{{ selectedProjects.length }} 个项目待你确认</span>
            <div>
              <button class="button-ghost" type="button" :disabled="importing" @click="emit('close')">拒绝</button>
              <button class="button-primary" type="button" :disabled="!selectedProjects.length || importing" @click="emit('import', selectedProjects)">
                {{ importing ? '正在纳入…' : '确认纳入' }}
              </button>
            </div>
          </footer>
        </section>
      </div>
    </Transition>
  </Teleport>
</template>
