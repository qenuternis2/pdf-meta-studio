# Repository instructions

Read [README.md](README.md) for architecture, setup and validation commands.
Repository documentation and development work use English; the application UI
remains Russian.

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
