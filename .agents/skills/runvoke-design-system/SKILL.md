---
name: runvoke-design-system
description: Apply and protect Runvoke's warm, restrained desktop design system when creating, refactoring, or reviewing Vue pages, components, dialogs, menus, forms, settings, sidebars, logs, responsive layouts, light/dark themes, typography, colors, spacing, and interaction states.
---

# Runvoke Design System

Keep Runvoke calm, warm, compact, and operationally clear. Extend the existing visual language instead of introducing a separate style or generic component-library appearance.

## Start with the source of truth

1. Read `AGENTS.md`, `src/style.css`, and the Vue component being changed.
2. Read [references/design-spec.md](references/design-spec.md) completely before making visual changes.
3. Inspect nearby components for an existing pattern before inventing a new one.
4. Preserve the current Vue 3, TypeScript, global token, and class naming approach. Do not add a UI framework for a local interface change.

## Design workflow

1. Identify the page hierarchy, primary action, destructive action, status information, and scroll owner.
2. Reuse semantic CSS variables and existing control patterns. Add a token only when the value is shared and represents a stable role.
3. Use flat sections, dividers, and whitespace for grouping. Reserve a bordered surface for a real boundary such as a terminal, popover, form, or status message.
4. Implement light and dark modes together. Avoid fixed light colors unless a dark-theme override is added in the same change.
5. Keep resizing fluid with `clamp()`, flexible tracks, and `minmax(0, 1fr)`. Do not hide structural jumps behind delayed width transitions.
6. Preserve task state, logs, form data, focus behavior, and existing commands while changing presentation.

## Non-negotiable visual rules

- Use warm neutral surfaces and muted sage green as the primary accent.
- Keep the interface minimal and mature: no cyberpunk styling, neon glow, purple gradients, excessive glass, emoji icons, or decorative noise.
- Avoid card-inside-card layouts and grids of unrelated floating cards.
- Use small radii, fine borders, restrained shadows, and compact desktop typography.
- Use color plus text or shape for status; never rely on color alone.
- Keep terminal surfaces deliberately darker than the application canvas in both themes.
- Keep settings as a sidebar entry only; hide the sidebar while the full-width settings page is open and restore it when returning to the workspace.

## Interaction rules

- Give every interactive element a visible hover, focus, active, disabled, and busy state where applicable.
- Keep micro-interactions between 140–220 ms. Animate opacity, transform, border, and color; avoid layout-shifting hover effects.
- Use explicit confirmation for destructive or process-stopping actions.
- Preserve keyboard semantics with native buttons, inputs, labels, `role`, `aria-checked`, and `aria-current` as appropriate.
- Respect `prefers-reduced-motion`.

## Validation

After a UI change:

1. Run `pnpm typecheck` and `pnpm build`.
2. Check light and dark themes.
3. Check the normal `1120 × 720` window and minimum `840 × 560` window.
4. Verify no horizontal overflow, clipped primary actions, sidebar flash during settings mode, or breakpoint jumps.
5. Exercise changed controls and confirm existing Tauri handlers still receive the same intent.
6. Run `git diff --check` and remove temporary screenshots or browser artifacts.
7. Update `PROJECT_OVERVIEW.md`, `.agents/PROJECT_PROMPT.md`, `AGENTS.md`, and `.agents/skills/runvoke/SKILL.md` only when product scope, architecture, directory conventions, or acceptance criteria change.
