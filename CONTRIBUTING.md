# Contributing

Thanks for helping! Issues and pull requests are welcome.

## Ground rules

- **Never** include game assets, client/server files, PAK keys or `studio.config.json` in issues, PRs or commits.
- Keep changes focused; one topic per PR.
- The tool never modifies originals: every write goes to a new output folder. Please keep that safety model.

## Building

```powershell
.\build.ps1 -OutputDirectory .\dist
```

Requires the .NET 8 SDK and Python 3. See the README for the native `pak-tool.exe`.

## Reporting bugs

Use the issue templates. Include the app version, Windows version and steps to reproduce. Redact any paths or data you do not want public.
