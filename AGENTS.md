# Repository instructions

Read [README.md](README.md) for architecture, setup and validation commands.
Repository documentation and development work use English; the application UI
remains Russian.

## Build delivery

- Include the application version in every archive delivered to the user (for
  example, `PdfMetaStudio-v0.3.2-win-x64.zip`) and in the accompanying message or
  download-link label. Preserve versioned release assets and checksum files.
- Deliver the standalone per-user EXE installer as the primary installation asset
  (for example, `PdfMetaStudio-0.3.4-win-x64-setup.exe`), rather than an MSI wrapper.
  Keep the version in its filename and delivery-link label.
- The main window title includes the built application version in parentheses.
  Read it from assembly metadata so a version bump updates the title automatically.

## Interface reviews

- When developing or changing a web interface, use
  [web-design-guidelines](.agents/skills/web-design-guidelines/SKILL.md) to review
  the result before completing the task.
- For UI, UX or accessibility audit requests, explicitly apply this skill and
  state that it is being used. Fetch its current rules before every review and
  report findings using the skill's output format.
- The current application uses WPF. Apply relevant UX and accessibility criteria
  to native UI reviews, identify web-specific rules that do not apply, and use
  appropriate native checks. A web-guidelines review alone does not establish WPF
  accessibility compliance.
- If current rules cannot be fetched, explain the network limitation and required
  domains; do not claim that the current guidelines were checked. See the skill's
  [provenance README](.agents/skills/web-design-guidelines/README.md) for the pinned
  source, network requirements and reproducible update procedure.
