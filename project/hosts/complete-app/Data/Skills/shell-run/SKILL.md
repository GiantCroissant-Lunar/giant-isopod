---
name: shell-run
description: Execute shell commands for builds, tests, and system operations
metadata:
  author: giant-isopod
  version: "1.0.0"
  capabilities:
    - shell_run
    - test_run
    - verify_build
allowed-tools:
  - bash
tags:
  - shell
  - testing
  - build
---

# Shell Run Skill

You can execute shell commands. Use this for:

1. Running build commands
2. Executing test suites
3. Checking system state
4. Installing dependencies
