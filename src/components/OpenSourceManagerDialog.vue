<script setup lang="ts">
import { computed, nextTick, ref, useTemplateRef, watch } from "vue";
import { open } from "@tauri-apps/plugin-dialog";
import OverflowTooltip from "./OverflowTooltip.vue";
import type { OpenSourceConfig } from "../types";

const props = defineProps<{
    open: boolean;
    sources: OpenSourceConfig[];
}>();

const emit = defineEmits<{
    close: [];
    save: [source: OpenSourceConfig];
    delete: [sourceId: string];
}>();

const drafts = ref<OpenSourceConfig[]>([]);
const selectedSourceId = ref<string | null>(null);
const deleteConfirmationOpen = ref(false);
const dialog = useTemplateRef<HTMLElement>("dialog");
const nameInput = useTemplateRef<HTMLInputElement>("nameInput");
const deleteCancelButton =
    useTemplateRef<HTMLButtonElement>("deleteCancelButton");

const selectedSource = computed(() =>
    drafts.value.find((source) => source.id === selectedSourceId.value),
);

const selectedSourceIsSaved = computed(() =>
    props.sources.some((source) => source.id === selectedSourceId.value),
);

const selectedSourceIsBuiltIn = computed(
    () =>
        selectedSource.value?.id === "vscode" ||
        selectedSource.value?.id === "zed",
);

const validationMessage = computed(() => {
    const source = selectedSource.value;
    if (!source) return "请先选择一个软件源";
    if (!source.name.trim()) return "请输入显示名称";
    if (!source.executable.trim()) return "请输入程序路径或命令";
    if (!source.arguments.includes("{directory}"))
        return "打开目录的参数中必须包含 {directory}";
    return "";
});

function syncSources() {
    const previousId = selectedSourceId.value;
    drafts.value = props.sources.map((source) => ({ ...source }));
    selectedSourceId.value = drafts.value.some(
        (source) => source.id === previousId,
    )
        ? previousId
        : (drafts.value[0]?.id ?? null);
    deleteConfirmationOpen.value = false;
}

watch(
    () => props.sources,
    () => {
        if (props.open) syncSources();
    },
    { deep: true },
);

watch(
    () => props.open,
    (open) => {
        if (!open) return;
        syncSources();
        void nextTick(() => dialog.value?.focus());
    },
    { immediate: true },
);

function selectSource(sourceId: string) {
    selectedSourceId.value = sourceId;
    deleteConfirmationOpen.value = false;
}

function addSource() {
    const unsavedSource = drafts.value.find(
        (source) => !props.sources.some((item) => item.id === source.id),
    );
    if (unsavedSource) {
        selectedSourceId.value = unsavedSource.id;
        deleteConfirmationOpen.value = false;
        void nextTick(() => nameInput.value?.focus());
        return;
    }

    const source: OpenSourceConfig = {
        id: `custom-${Date.now().toString(36)}`,
        name: "新软件",
        executable: "",
        arguments: '"{directory}"',
    };
    drafts.value.push(source);
    selectedSourceId.value = source.id;
    deleteConfirmationOpen.value = false;
    void nextTick(() => {
        nameInput.value?.focus();
        nameInput.value?.select();
    });
}

function saveSelectedSource() {
    const source = selectedSource.value;
    if (!source || validationMessage.value) return;
    emit("save", { ...source });
}

function requestDeleteSelectedSource() {
    if (!selectedSource.value) return;
    deleteConfirmationOpen.value = true;
    void nextTick(() => deleteCancelButton.value?.focus());
}

function confirmDeleteSelectedSource() {
    const source = selectedSource.value;
    if (!source) return;

    deleteConfirmationOpen.value = false;
    if (selectedSourceIsSaved.value) {
        emit("delete", source.id);
        return;
    }

    drafts.value = drafts.value.filter((item) => item.id !== source.id);
    selectedSourceId.value = drafts.value[0]?.id ?? null;
}

async function chooseExecutable() {
    const source = selectedSource.value;
    if (!source) return;
    try {
        const selected = await open({
            multiple: false,
            directory: false,
            title: `选择${source.name || "软件"}的可执行文件`,
            filters: [
                {
                    name: "应用程序",
                    extensions: ["exe", "lnk", "app", "bin"],
                },
            ],
        });
        if (typeof selected === "string") source.executable = selected;
    } catch {
        // 浏览器预览中可能没有原生文件选择器，用户取消时也无需提示。
    }
}

function handleDialogKeydown(event: KeyboardEvent) {
    if (event.key === "Escape") {
        event.stopPropagation();
        emit("close");
        return;
    }
    if (event.key !== "Tab" || !dialog.value) return;

    const focusable = Array.from(
        dialog.value.querySelectorAll<HTMLElement>(
            'button:not(:disabled), input:not(:disabled), [tabindex]:not([tabindex="-1"])',
        ),
    );
    if (!focusable.length) {
        event.preventDefault();
        return;
    }

    const first = focusable[0];
    const last = focusable.at(-1);
    if (
        event.shiftKey &&
        (document.activeElement === first || document.activeElement === dialog.value)
    ) {
        event.preventDefault();
        last?.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first?.focus();
    }
}
</script>

<template>
    <Teleport to="body">
        <Transition name="modal">
            <div
                v-if="open"
                class="modal-layer open-source-modal-layer"
                @mousedown.self="emit('close')"
            >
                <section
                    ref="dialog"
                    class="open-source-dialog"
                    role="dialog"
                    aria-modal="true"
                    aria-labelledby="open-source-dialog-title"
                    aria-describedby="open-source-dialog-description"
                    tabindex="-1"
                    @keydown="handleDialogKeydown"
                >
                    <header class="open-source-dialog-header">
                        <div>
                            <span class="section-kicker">项目配置 / 打开方式</span>
                            <h2 id="open-source-dialog-title">管理打开方式</h2>
                            <p id="open-source-dialog-description">
                                配置项目菜单中用于打开目录的编辑器或其他软件。
                            </p>
                        </div>
                        <button
                            class="icon-button"
                            type="button"
                            aria-label="关闭"
                            title="关闭"
                            @click="emit('close')"
                        >
                            ×
                        </button>
                    </header>

                    <div class="open-source-dialog-body">
                        <aside
                            class="open-source-navigation"
                            aria-label="软件源列表"
                        >
                            <div class="open-source-navigation-heading">
                                <div>
                                    <strong>软件列表</strong>
                                    <span>{{ drafts.length }} 个来源</span>
                                </div>
                                <button type="button" @click="addSource">
                                    <i aria-hidden="true">+</i>
                                    添加
                                </button>
                            </div>

                            <div
                                v-if="drafts.length"
                                class="open-source-navigation-list"
                            >
                                <button
                                    v-for="source in drafts"
                                    :key="source.id"
                                    class="open-source-navigation-item"
                                    :class="{
                                        active:
                                            selectedSourceId === source.id,
                                    }"
                                    type="button"
                                    :aria-current="
                                        selectedSourceId === source.id
                                            ? 'true'
                                            : undefined
                                    "
                                    @click="selectSource(source.id)"
                                >
                                    <span class="open-source-navigation-copy">
                                        <span>
                                            <OverflowTooltip
                                                as="strong"
                                                :text="
                                                    source.name || '未命名软件'
                                                "
                                            >
                                                {{
                                                    source.name || "未命名软件"
                                                }}
                                            </OverflowTooltip>
                                            <small
                                                v-if="
                                                    source.id === 'vscode' ||
                                                    source.id === 'zed'
                                                "
                                                >内置</small
                                            >
                                            <small
                                                v-else-if="
                                                    !sources.some(
                                                        (item) =>
                                                            item.id ===
                                                            source.id,
                                                    )
                                                "
                                                >待保存</small
                                            >
                                        </span>
                                        <OverflowTooltip
                                            as="code"
                                            :text="
                                                source.executable ||
                                                '尚未配置程序'
                                            "
                                        >
                                            {{
                                                source.executable ||
                                                "尚未配置程序"
                                            }}
                                        </OverflowTooltip>
                                    </span>
                                    <i aria-hidden="true" />
                                </button>
                            </div>

                            <div v-else class="open-source-navigation-empty">
                                <span>尚无软件源</span>
                                <button type="button" @click="addSource">
                                    添加第一个
                                </button>
                            </div>
                        </aside>

                        <div class="open-source-config-panel">
                            <template v-if="selectedSource">
                                <div class="open-source-config-heading">
                                    <div>
                                        <span>当前配置</span>
                                        <strong>{{
                                            selectedSource.name || "未命名软件"
                                        }}</strong>
                                    </div>
                                    <small>{{
                                        selectedSourceIsBuiltIn
                                            ? "内置来源"
                                            : "自定义来源"
                                    }}</small>
                                </div>

                                <div class="open-source-config-fields">
                                    <label>
                                        <span>显示名称</span>
                                        <input
                                            ref="nameInput"
                                            v-model="selectedSource.name"
                                            type="text"
                                            maxlength="120"
                                            autocomplete="off"
                                            placeholder="例如 Zed"
                                        />
                                    </label>
                                    <label>
                                        <span>程序路径或命令</span>
                                        <div class="open-source-executable-field">
                                            <input
                                                v-model="
                                                    selectedSource.executable
                                                "
                                                type="text"
                                                maxlength="1024"
                                                autocomplete="off"
                                                placeholder="例如 zed 或完整路径"
                                            />
                                            <button
                                                type="button"
                                                @click="chooseExecutable"
                                            >
                                                选择文件
                                            </button>
                                        </div>
                                    </label>
                                    <label>
                                        <span>打开目录的参数</span>
                                        <input
                                            v-model="selectedSource.arguments"
                                            type="text"
                                            maxlength="4096"
                                            autocomplete="off"
                                            placeholder='例如 "{directory}" 或 --reuse-window "{directory}"'
                                        />
                                        <small>
                                            使用
                                            <code>{directory}</code>
                                            代表当前项目目录。
                                        </small>
                                    </label>
                                </div>

                                <div class="open-source-config-note">
                                    <i aria-hidden="true">i</i>
                                    <p>
                                        软件会被直接启动，不经过 shell。若提示找不到程序，请选择可执行文件，或确认命令已加入
                                        PATH。
                                    </p>
                                </div>
                            </template>

                            <div v-else class="open-source-config-empty">
                                <strong>选择一个软件源开始配置</strong>
                                <p>也可以从左侧添加新的编辑器或其他软件。</p>
                            </div>
                        </div>
                    </div>

                    <footer
                        class="open-source-dialog-footer"
                        :class="{ confirming: deleteConfirmationOpen }"
                    >
                        <template v-if="deleteConfirmationOpen">
                            <p>
                                确定删除“{{
                                    selectedSource?.name || "未命名软件"
                                }}”吗？
                            </p>
                            <div>
                                <button
                                    ref="deleteCancelButton"
                                    class="button-ghost"
                                    type="button"
                                    @click="deleteConfirmationOpen = false"
                                >
                                    取消
                                </button>
                                <button
                                    class="open-source-delete-confirm"
                                    type="button"
                                    @click="confirmDeleteSelectedSource"
                                >
                                    确认删除
                                </button>
                            </div>
                        </template>
                        <template v-else>
                            <div class="open-source-footer-status">
                                <button
                                    class="open-source-delete-button"
                                    type="button"
                                    :disabled="!selectedSource"
                                    @click="requestDeleteSelectedSource"
                                >
                                    删除此软件源
                                </button>
                                <small
                                    v-if="validationMessage"
                                    role="status"
                                    >{{ validationMessage }}</small
                                >
                            </div>
                            <div>
                                <button
                                    class="button-ghost"
                                    type="button"
                                    @click="emit('close')"
                                >
                                    关闭
                                </button>
                                <button
                                    class="button-primary"
                                    type="button"
                                    :disabled="
                                        !selectedSource || !!validationMessage
                                    "
                                    :title="validationMessage || undefined"
                                    @click="saveSelectedSource"
                                >
                                    保存更改
                                </button>
                            </div>
                        </template>
                    </footer>
                </section>
            </div>
        </Transition>
    </Teleport>
</template>
