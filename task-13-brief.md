# Task 13 brief: offline PNG card themes

## Bounded scope

- Add a local-only PNG import/reset flow backed by the existing SQLite app-settings store.
- Validate extension, PNG signature/decoder, file size and dimensions before copying into the app skin directory.
- Persist one shared theme (image path plus clamped opacity) and restore it at startup.
- Render the same Stretch-filled image and readability veil over the floating card and relation drawer, preserving accessible foreground contrast.
- Keep the default theme reversible and do not modify pronunciation behavior.

## Existing entry points

- `PngThemeService` and `ThemeSettingsViewModel` already exist as uncommitted worktree changes; tests currently cover validator and opacity clamp only.
- `ServiceRegistration` already registers the theme service/view model.
- `FloatingCardWindow` has a `ThemeImageLayer` and `ThemeReadabilityVeil` placeholder, but the theme is not yet connected to a shared visual source or drawer.
- `RelationDrawer` currently owns its own surface brushes and has no theme binding.
- `App.OnStartup`/`CreateCardAndTrayAsync` are the startup and card construction hooks for restore/application.

## TDD acceptance tests

1. Valid local PNG import copies to the skin directory, persists path and clamped opacity, and restores after a new service instance.
2. Invalid extension, malformed PNG, oversized file, and oversized dimensions are rejected without changing the active persisted theme.
3. Reset clears the persisted image and returns the default theme.
4. Theme state is shared by card and relation drawer; image stretches to all corners and opacity is observable.
5. Theme controls expose import, opacity, reset and validation feedback with accessible names/contrast.

## Non-goals

- No remote images, JPEG/WebP, cropping/editor, per-window themes, or pronunciation changes.
