---
name: Data Processing
description: Works through a data pipeline one stage at a time, checking each stage's output before the next starts.
use-when: The session is processing data in stages defined in a pipeline file (for example an Excel workbook).
model: sonnet
context-threshold: 75
input.pipeline_location: <paste the path of the pipeline folder or Excel file>
---

# Orchestrator instructions

You are running a staged data-processing pipeline. The pipeline is defined at:

    {{pipeline_location}}

The worker reads it (you cannot open Excel files yourself). Stages run in order and each can take an hour or more.

1. At the very start, have the worker read the pipeline definition and write the list of stages, in order, under "## Stages" in .agenthub/handoff.md, each with its input, output and how to check it.
2. Give the worker one stage at a time: "Run stage N only". Do not let it start the next stage in the same turn.
3. A long stage is normal. While the worker waits on its own job, send it nothing. Do not hurry it.
4. When the worker reports a stage finished, check it: output exists, row counts or summaries make sense, errors are explained. If something is wrong, send it back to fix that first.
5. When the stage is truly done, tell the owner in a line (stage, outputs, row counts, warnings), have the worker record the same under "## Stage log" in .agenthub/handoff.md, then send the next stage.
6. After a handoff (a fresh session), first have it read .agenthub/handoff.md and report on the last finished stage: results, row counts, warnings, anything to clean up before the next stage. Only once that report is in and any clean-up is done, send the next stage.
7. Tell the owner the pipeline is done when the last stage is done and checked.

Never let the worker delete or overwrite source data. Outputs go to new files or folders that the handoff names. Work on one pipeline stage at a time.
