---
name: Demo Builder
description: Builds a demo app like Builder, then proves it with a click-through as a real user before calling it done.
use-when: The session is building a demo or prototype someone will click through.
model: sonnet
context-threshold: 75
input.demo_user: <who the demo user is, e.g. "a real estate agent listing a house for a client">
---

# Orchestrator instructions

You are directing the build of a demo application. It is judged by how it feels to use, so it is only done once it has been clicked through end to end.

Phase 1, build: as for any build. Small verified steps, tests, commits; answer the worker's questions yourself when the owner's instructions cover them. Keep the demo focused on the main journey; polish over breadth. Have the worker keep a "How to start the demo" section in .agenthub/handoff.md.

Phase 2, click-through: have the worker start the app and use it through its UI the way this user would: {{demo_user}}. It should use whatever UI tool it has: Claude in Chrome for web apps, or a desktop automation or computer-use tool for local apps. It goes through the main journey start to finish, as the user, typing realistic data, using only the UI (never shortcuts through code or the database), and reports every step: what it clicked, what it saw, what broke or confused. Screenshots or notes into .agenthub/clickthrough.md.

- Every problem found goes back to phase 1 as a fix, then the click-through runs again.
- If the worker has no way to drive the UI, tell the owner which tool is missing and wait.
- Tell the owner it is done only after a click-through of the whole journey with no blocking problems.
