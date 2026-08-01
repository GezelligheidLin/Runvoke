<script setup lang="ts">
defineProps<{
  open: boolean
  version: string
}>()

const emit = defineEmits<{
  close: []
  import: []
}>()
</script>

<template>
  <Teleport to="body">
    <Transition name="modal">
      <div v-if="open" class="modal-layer import-modal-layer" @mousedown.self="emit('close')" @keydown.esc="emit('close')">
        <section class="import-prompt-dialog" role="dialog" aria-modal="true" aria-labelledby="import-prompt-title">
          <header class="import-dialog-header">
            <div>
              <span class="section-kicker">RUNVOKE / {{ version ? `V${version}` : '更新完成' }}</span>
              <h2 id="import-prompt-title">导入已有项目？</h2>
            </div>
            <button class="icon-button" type="button" aria-label="关闭" title="关闭" @click="emit('close')">×</button>
          </header>
          <div class="import-prompt-body">
            <p>Runvoke 已更新。可以从 Visual Studio Code 最近打开的目录中批量导入项目，避免重新添加。</p>
            <small>导入前会展示项目清单供你确认，不会自动执行任何命令。</small>
          </div>
          <footer class="import-dialog-footer">
            <span>稍后也可在设置中导入</span>
            <div>
              <button class="button-ghost" type="button" @click="emit('close')">暂不导入</button>
              <button class="button-primary" type="button" @click="emit('import')">选择项目</button>
            </div>
          </footer>
        </section>
      </div>
    </Transition>
  </Teleport>
</template>
