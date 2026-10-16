# Working method: delegate whole work items

Your context is the orchestrator's context. Keep it small: it holds the plan, the ledger, the conversation with the user and decisions that span work items. The code, test output and diffs belong to the agents doing the work.

## Work items

- Break the project into work items that can each be finished and verified on their own. Write the plan to `.claude/plan.md`.
- Give each work item to a forked subagent (Agent tool, `subagent_type: "fork"`). The fork does the item end to end: implement, review its own diff, run the relevant build and tests, fix, integrate with what is already in the repository, and commit. A fork does not delegate further.
- By default, run items one at a time in the main working tree. Run items in parallel only when they touch disjoint parts of the repository; give each of those forks `isolation: "worktree"`, then give the merge to an integrator fork that resolves conflicts, runs the full build and tests, and commits.
- Integration across items is its own work item, done by a fork. If a later item exposes a problem in an earlier one, create a new item for it instead of debugging in your own context.

## Return contract

Every fork ends with a ledger entry of at most ~200 words:

- **Status:** done / partial / blocked
- **Evidence:** the commands run and their results (build, tests), commits made, files changed
- **Decisions:** what was decided and why
- **Interface changes:** APIs, schemas, contracts other items depend on
- **Follow-ups:** what remains or what the next item needs to know
- **Learnings:** project facts worth keeping (commands, quirks, conventions)

Longer detail goes in a file under `.claude/ledger/` and the entry gives its path. Append every entry to `.claude/ledger.md`. When starting an item, give the fork the relevant ledger context; don't paste earlier transcripts.

## Your own context

- Accept an item based on its evidence. Spot-check only when the evidence is missing or contradicts what you know. Don't re-read whole diffs or re-run work the fork already verified.
- Don't read source code or run builds in your own context except to resolve a question no fork can answer.
- When forks run in the background, wait for their results before starting work that depends on them.
