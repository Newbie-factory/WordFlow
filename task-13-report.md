# Task 13 report: offline PNG card themes

Implemented the bounded local PNG theme flow.

- Validates `.png` extension, PNG signature/decoder, file size (up to 20 MB), and dimensions (up to 8192 px).
- Copies validated files into the per-user `Skins` directory and persists image path plus clamped opacity in SQLite app settings.
- Restores the persisted theme at card startup; reset returns to the default surface.
- Applies one shared stretch-filled image and readability veil to the floating card and relation drawers.
- Added import/restore/reset, malformed-file, and opacity tests.

Verification: `dotnet test tests/WordFlow.App.Tests/WordFlow.App.Tests.csproj --no-restore` — 112 passed.
