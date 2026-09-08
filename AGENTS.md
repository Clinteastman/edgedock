# EdgeDock development

Build a native Windows dashboard for a 2560x720 touch display. Read PRODUCT.md and DESIGN.md before UI changes. Keep existing Windows applications separate; do not recreate or embed them.

Use suitable GPT-5.6 workers with explicit bounded briefs to reduce coordinator usage: Terra for research and review, Sol for implementation/debugging, Luna for mechanical checks. Default medium reasoning, no recursive delegation. Keep unrelated files intact.

Use stable SDKs, no telemetry, no hard-coded private URLs, credentials or user paths in committed code. Web sign-in stays in the local WebView2 profile. Never bypass TLS errors. Only navigate http/https URLs from user configuration.

Run build and relevant checks. Report compilation separately from actual Windows runtime and physical touchscreen verification. Review public content for private data before publishing. Keep documentation concise and honest about limitations.
