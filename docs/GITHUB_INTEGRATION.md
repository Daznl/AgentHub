# GitHub Integration: Accounts, Sign-in and the Access-Aware Repository Browser

Status: shipped September 2026 (v0.1.6). This document records what was built, why it was built
that way, and the gotchas discovered along the way so the next person (or agent) keeps the context.

## 1. Guiding rule

**AgentHub never owns a GitHub credential.** The GitHub CLI (`gh`) owns tokens; Git Credential
Manager owns `git push`/`pull` credentials. AgentHub only *drives* `gh` and *reads* what `gh` reports.
Every feature below is a thin, non-interactive wrapper over a `gh` command. If `gh` cannot do it,
AgentHub does not do it.

## 2. Components

```text
MainWindow (GitHub Repos view)
    |
    +-- Account header
    |     +-- GitHubAccountCombo ---- lists gh accounts, selection => gh auth switch
    |     +-- "Sign in" button ------- opens GitHubLoginWindow
    |     +-- "Sign out" button ------ gh auth logout --hostname H --user U (confirm first)
    |
    +-- GitHubLoginWindow (modal) --- drives gh auth login --web, shows one-time code
    |
    +-- GitHubRepoList (virtualised ListBox, grouped by access category)
    |
    +-- GitHubService
          +-- GetAccountsAsync ---------- gh auth status --json hosts
          +-- SwitchAccountAsync -------- gh auth switch --hostname H --user U
          +-- LogoutAsync --------------- gh auth logout --hostname H --user U
          +-- LoginWithBrowserAsync ----- gh auth login --hostname H --git-protocol https --web --skip-ssh-key
          +-- StreamAllRepositoriesAsync  gh api graphql --paginate (viewer.repositories, all affiliations)
          +-- GetAuthUserAsync ---------- gh api user --jq .login  (the "is anyone signed in" probe)
          |
          +-- ProcessRunner.RunStreamingAsync  (per-line callback; UTF-8 decoding)

Models
    +-- GitHubAccount ------- host, login, active, state, tokenSource, gitProtocol, scopes
    +-- GitHubRepository ---- + isArchived, isFork, isInOrganization, viewerPermission, owner{login,__typename}
                              + derived: CanPush, Category, CategoryOrder, AccessLabel, OwnerLabel ...
    +-- GitHubRepositoryOwner
```

Files: `src/AgentHub/Services/GitHubService.cs`, `src/AgentHub/Services/GitHubLoginProgress.cs`,
`src/AgentHub/Services/ProcessRunner.cs`, `src/AgentHub/GitHubLoginWindow.xaml(.cs)`,
`src/AgentHub/Models/GitHubAccount.cs`, `src/AgentHub/Models/GitHubRepository.cs`,
`src/AgentHub/MainWindow.xaml(.cs)` (GitHub view section), `tests/AgentHub.Tests/GitHubServiceTests.cs`.

## 3. Sign-in: real browser verification without a terminal

### How it works

1. `GitHubLoginWindow` asks for a host (default `github.com`; GitHub Enterprise Server hosts work too).
2. `GitHubService.LoginWithBrowserAsync` starts
   `gh auth login --hostname <host> --git-protocol https --web --skip-ssh-key`
   with **stdin closed**. gh detects it is not interactive and therefore:
   - asks no questions (protocol, SSH key, git credential helper are all skipped or fixed by flags);
   - prints `! First copy your one-time code: XXXX-XXXX` and
     `Open this URL to continue in your web browser: https://github.com/login/device` to **stderr**;
   - does **not** wait for Enter and does **not** open a browser itself;
   - polls GitHub until the user approves or the code expires (~15 min), then prints
     `✓ Logged in as <login>`.
3. `ProcessRunner.RunStreamingAsync` feeds every stdout/stderr line to a callback as it arrives.
   Three regexes pull out the code, the URL and the final login. The dialog shows the code in a
   large badge, copies it to the clipboard, opens the URL in the default browser once, and offers
   Copy / Open buttons to repeat either.
4. On exit code 0 the dialog closes with `DialogResult = true`; `MainWindow` reloads accounts and
   repositories. Cancel (or closing the window) cancels the token, which kills the `gh` process tree.

This is the genuine GitHub device flow, performed by gh against GitHub. AgentHub sees the one-time
code (which is public by design) and never the token.

### Why not the terminal pane?

The original v0.1.5 behaviour spawned `gh auth login` interactively in a Cockpit ConPTY pane. That
still exists as the secondary "Use terminal instead" button on the not-signed-in banner, for people
who want SSH keys or a non-default protocol. The in-app dialog is the default because the terminal
route requires reading prompts inside an embedded terminal and was confusing for new users.

### Multiple accounts

gh keeps one token per (host, login) and one *active* account per host. Signing in again with a
different account **adds** it and makes it active; the previous account stays available.

- The account dropdown is populated from `gh auth status --json hosts` (always exits 0; each entry
  carries `state` so an expired token shows a ⚠ suffix).
- Picking a different entry runs `gh auth switch --hostname H --user U`, then reloads the list.
  `_suppressAccountSelection` guards against the programmatic repopulation firing the handler.
- Sign out runs `gh auth logout --hostname H --user U` after a Yes/No confirmation.

**Gotcha:** switching the active gh account changes what `gh` (and therefore the browser) sees, but
`git push`/`git pull` use whatever Git Credential Manager has cached. If a user wants git to follow
the active gh account they must run `gh auth setup-git` once; AgentHub deliberately does not edit
global git config as a side effect.

## 4. Repository browser: load everything, categorise by what you may do

### The bug that prompted the rewrite

`gh repo list` **with no owner argument lists only repositories the account personally owns**. For an
IGO staff account that is typically zero: every organisation repo was invisible, and the view
appeared to "hang" on the loading text. Two further problems compounded it: the list was an
`ItemsControl` inside a `ScrollViewer` (no virtualisation, so hundreds of cards rendered at once) and
`ProcessRunner` had a 30-second default timeout.

### One query for everything, streamed

`StreamAllRepositoriesAsync` runs a single paginated GraphQL query via `gh api graphql --paginate`:

```graphql
query($endCursor: String) {
  viewer {
    repositories(first: 100, after: $endCursor,
                 affiliations: [OWNER, COLLABORATOR, ORGANIZATION_MEMBER],
                 ownerAffiliations: [OWNER, COLLABORATOR, ORGANIZATION_MEMBER],
                 orderBy: {field: PUSHED_AT, direction: DESC}) {
      pageInfo { hasNextPage endCursor }
      nodes { name nameWithOwner description url isPrivate isArchived isFork isInOrganization
              pushedAt updatedAt viewerPermission owner { login __typename } }
    }
  }
}
```

with `--jq '.data.viewer.repositories.nodes[]'`, which makes gh emit **one compact JSON object per
line, page by page**. `ParseRepositoryLine` deserialises each line; non-JSON lines (gh warnings) are
ignored. `MainWindow` receives repos through a `Progress<T>` (marshalled to the UI thread), buffers
them, and re-renders every 25 repos or 400 ms, so a large organisation shows its first page within a
second or two. Measured: 121 repos in ~3.6 s.

Refresh or an account switch cancels any in-flight load (`_gitHubLoadCts`) so repositories from the
previous account cannot bleed into the new list. `_gitHubLoading` stops a second click on
"GitHub Repos" from starting a duplicate load.

### Permission model

`viewerPermission` is GitHub's own answer for the signed-in user on that repository. AgentHub never
infers rights from ownership or naming.

| viewerPermission | Archived? | CanPush | Badge | Category (group header) |
|---|---|---|---|---|
| ADMIN | no | yes | 🔑 Admin · push | 👤 My repositories / 🏢 Org · you can push |
| MAINTAIN | no | yes | 🔧 Maintain · push | same as above |
| WRITE | no | yes | ✎ Push access | same as above |
| TRIAGE / READ / null | no | no | 👁 Read-only · clone | 🏢 Org · clone only (read access) / 🤝 Shared · clone only |
| any | **yes** | **no** | 📦 Archived · read-only | 📦 Archived · clone or pull only |

Ownership: `IsOwnedByViewer` = `owner.login == active login` (case-insensitive, set at parse time).
`IsOwnedByOrganization` = `isInOrganization` or `owner.__typename == "Organization"`.

Group order (`CategoryOrder`): mine (0) → org pushable (10) → shared pushable (11) →
org read-only (20) → shared read-only (21) → archived (40). Within a group, newest push first.

What the user can do:

- **Clone & Add** — always available. Everything visible can be cloned.
- **Pull (fast-forward)** — available whenever a local clone exists and is behind; read access suffices.
- **Push** — button rendered only when `CanPush`; the click handler re-checks and shows the access
  tooltip if not. When there are local commits ahead of origin on a read-only repo, a red
  "⬆ Push blocked · read-only" marker is shown instead of a button.
- **Open in Cockpit / Open on GitHub** — unchanged.

### UI

- `GitHubRepoList` is a `ListBox` with `VirtualizingPanel.IsVirtualizing`,
  `IsVirtualizingWhenGrouping`, `VirtualizationMode=Recycling`, `ScrollUnit=Pixel` and a bare
  `ContentPresenter` item template (no selection highlight). `ItemsSource` is a `ListCollectionView`
  with sort descriptions (CategoryOrder, Category, PushedAt desc) and a `PropertyGroupDescription`
  on `Category`. Group headers show the category name and item count.
- The filter combo uses `Tag` values rather than indices (`push`, `readonly`, `mine`, `org`,
  `archived`, then the sync filters). Search matches name, description and owner.
- The header line summarises counts: `121 repositories · push access on 16 · clone-only on 105`.
- Each card shows: visibility badge, access badge (tooltip explains the role), In AgentHub badge,
  sync badge, owner label (👤 Yours / 🏢 Org / 🤝 User), last push, local path.

### Gotchas recorded

1. `gh repo list` ≠ "all repos I can see". Use the GraphQL `viewer.repositories` query with all
   affiliations, or `gh repo list <org>` per organisation.
2. `ProcessStartInfo` defaults to the console codepage for redirected output. gh and git emit UTF-8;
   without `StandardOutputEncoding = UTF8` an em dash in a description became `â€"`. Fixed in both
   `ProcessRunner` methods.
3. WPF `ListBox` group headers rendered light text on a light band under the default theme. The
   `GroupStyle` now sets an explicit dark background on the header and a transparent `GroupItem`.
4. Emoji in WPF `TextBlock` render as monochrome glyphs (Segoe UI Emoji is not colour-rendered in
   WPF text). They are used as category glyphs only, never as the sole carrier of meaning.
5. Non-interactive `gh auth login` only works with `--hostname` and `--web` (or a token). Without
   them gh exits immediately asking for interactive input.
6. Bash heredocs on Windows truncated a C# file at a non-ASCII character (`✓`) during development.
   Write files containing non-ASCII with a proper editor/tool, not a shell heredoc.

## 5. Testing

`tests/AgentHub.Tests/GitHubServiceTests.cs` covers the pure functions:

- `ParseAccounts` — single host, multiple hosts/accounts (ordering, ⚠ on non-success state),
  empty/invalid JSON.
- `ParseRepositoryLine` — org READ repo is clone-only and categorised correctly; every
  `viewerPermission` value maps to the right `CanPush`/`CategoryOrder`; own repo detected
  case-insensitively even without `__typename`; archived ADMIN repo is never pushable; noise lines
  return null.
- `FirstMeaningfulLine` — strips gh's `!`/`✓`/`X`/`-` glyphs.

Process-driving paths (`LoginWithBrowserAsync`, `StreamAllRepositoriesAsync`) are exercised manually:
the sign-in dialog was verified end to end with a real account, and the repository view was driven
with UI Automation and screenshotted (121 repos, grouped, correct counts).

Manual verification of gh's non-interactive behaviour (kept here because it is load-bearing):

```powershell
# prints code + URL to stderr, does not wait for Enter, does not open a browser
gh auth login --hostname github.com --git-protocol https --web --skip-ssh-key < NUL
# account list shape
gh auth status --json hosts
```

## 6. Not done / future

- `gh auth setup-git` as an explicit, opt-in button so git credentials follow the active gh account.
- Per-organisation filter when a user belongs to several organisations (currently they are separate
  groups; search by owner name works).
- Remember the last selected filter per account.
- PR / CI / branch-protection views (roadmap v0.5) can reuse `StreamAllRepositoriesAsync`'s
  streaming pattern and the `viewerPermission` data.
- The `GetAuthUserAsync` probe still uses `gh api user` (network). `gh auth status --active` would be
  an offline alternative if proxies become a problem.
