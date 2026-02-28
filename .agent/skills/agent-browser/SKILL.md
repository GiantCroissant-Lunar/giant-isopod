---
name: agent-browser
description: Browser automation CLI for AI agents. Use when the user needs to interact with websites, including navigating pages, filling forms, clicking buttons, taking screenshots, extracting data, testing web apps, or automating any browser task.
---

# Browser Automation with agent-browser

Local installation at `.agent/skills/agent-browser/node_modules/.bin/agent-browser`.

Run commands using the local binary:

```bash
npx --prefix .agent/skills/agent-browser agent-browser <command>
```

## Setup (first time)

```bash
npx --prefix .agent/skills/agent-browser agent-browser install
```

## Core Workflow

1. **Navigate**: `npx --prefix .agent/skills/agent-browser agent-browser open <url>`
2. **Snapshot**: `npx --prefix .agent/skills/agent-browser agent-browser snapshot -i`
3. **Interact**: Use refs (`@e1`, `@e2`) to click, fill, select
4. **Re-snapshot**: After navigation or DOM changes, get fresh refs

## Essential Commands

```bash
# Navigation
agent-browser open <url>
agent-browser close

# Snapshot (get element refs)
agent-browser snapshot -i
agent-browser snapshot -i -C          # Include cursor-interactive elements

# Interaction (use @refs from snapshot)
agent-browser click @e1
agent-browser fill @e2 "text"
agent-browser select @e1 "option"
agent-browser press Enter

# Get information
agent-browser get text @e1
agent-browser get url
agent-browser get title

# Wait
agent-browser wait @e1
agent-browser wait --load networkidle

# Capture
agent-browser screenshot
agent-browser screenshot --annotate
agent-browser pdf output.pdf
```

## Ref Lifecycle

Refs are invalidated when the page changes. Always re-snapshot after clicking links, submitting forms, or dynamic content loading.

## Full Documentation

See the upstream SKILL.md at https://github.com/vercel-labs/agent-browser for complete command reference.
