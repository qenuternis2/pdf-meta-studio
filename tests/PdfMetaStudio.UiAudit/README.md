# Windows UI audit

This diagnostic executable hosts the actual application's EditorView and view models
in a disposable Windows GitHub Actions session. It complements the existing
real-application keyboard/calendar/theme/layout/stress acceptance; it does not
modify or fix application behavior. It uses synthetic PDFs and a native worker
from a verified successful Windows workflow whose native inputs still match.

Run the **Windows UI audit** workflow with `package_run_id` set to that run.
The managed application and harness are rebuilt from the selected source commit.
`additional_only` skips baseline GUI acceptance explicitly; it does not establish
baseline acceptance for that run. Use it only when those checks already passed
and application code is unchanged.
The `windows-ui-audit` artifact records native provenance, host architecture/DPI,
existing acceptance, machine-readable additional results and native client screenshots.

Additional checks cover per-tag/per-stream draft retention, close/save/reload guards,
tree expansion, two-way shared-XMP choice display, invalid-date focus, date controls
in the external UIA tree after section/size cycles, actual UIA naming,
observed WPF text contrast, status live-region events with an explicit-event
positive control, focused/scrollable XML actions at three constrained sizes, and
realized containers plus last-row/footer/Tab reachability for 1,000 review rows.
Layout reachability checks respect ancestor scroll clips as well as window bounds,
wait up to two seconds for asynchronous layout/scrolling, and record exact bounds.
The shared hosted-image desktop guard runs during baseline and additional checks
only on disposable Windows ARM GitHub Actions; both callers stop it in `finally`.
Light and dark cases use separate processes with the actual system application theme, restored by the wrapper in `finally`. The harness also attempts the actual Windows
high-contrast flag and restores the original user state in `finally`.

`PASS`, `FAIL`, `ERROR`, `BLOCKED` and `SKIP` are distinct. Any failed, blocked or
erroneous required check returns a nonzero exit code; findings are not converted
to successful checks. Every case is collected and checkpointed so one finding does not suppress
the rest. The synthetic audit fixture explicitly contains editable Info keys and two
XMP streams. Contrast uses captured client background pixels and the actual text
brush. Captures read the composited desktop at the verified foreground client's
physical position, after DWM synchronization; a window DC or transparent rendered
visual omits Fluent translucency and is not evidence of displayed contrast.
Foreground acquisition uses bounded asynchronous verification and the existing
hosted foreground-lock release; input-thread attachment is always detached.
Desktop bounds are checked; unavailable capture or keyboard focus is blocked
without suppressing unrelated model/provider checks.
A provider that cannot deliver the explicit UIA event is blocked rather
than evidence that the application emits no events.

The 150%/200% LayoutTransform scenarios are constrained-layout simulations, not
physical Windows DPI changes. Actual system DPI is recorded. UIA notifications
and accessible names do not establish Narrator speech or comprehension. High
contrast screenshots still require visual focus-ring review. Full list realization
proves lack of virtualization, not a hang; elapsed layout time is also recorded.
Hosted Windows ARM64 runs the x64 application under emulation. Physical x64,
standard-user/offline and real speech/DPI acceptance remain separate checks.
