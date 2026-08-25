[Русская версия](README.md)

# DnsToolWinForms

A WinForms app for managing a Windows DNS Server — covers what `dnsmgmt.msc` doesn't:
records inside a Zone Scope shown as a tree, client subnets, Query Resolution Policies,
remote management of another server with its own authentication, self-updating from GitHub.

Works through the built-in `DnsServer` PowerShell module. Author: foosber, 2026. v2.4.0.

## Requirements

- **.NET Framework 4.8** — usually already present on Windows Server 2016+.
- **The DNS Server role** wherever operations are pointed (locally or via "Target DNS
  server"). **RSAT: DNS Server Tools** if the app only manages remotely.
- **Administrator rights** are only needed for **local** mode (see below) — on failure the
  app itself offers to restart elevated via UAC.
- **Visual Studio 2019/2022** — only for building from source.

## Administrator rights — on demand, not always

The manifest is `asInvoker`: the app starts without a UAC prompt. Rights are only needed
when an operation runs **locally** (the DNS service on this same machine) — for remote
management (`-ComputerName`/`CimSession`), rights are checked on the target server's side
via Kerberos/NTLM; local elevation doesn't come into it at all.

If a local operation genuinely hits an access-denied wall, the app shows a "Administrator
rights required" dialog offering to restart via UAC (`Verb="runas"`). Shown once per
session, doesn't nag on repeated failures.

## Interface

- **Icons instead of text buttons** on all 4 tabs — drawn in-house via GDI+
  (`IconFactory.cs`). Actions needing input (create a zone/scope/record/subnet/policy) open
  a dialog via the "+" icon. The filter stays a plain field — live search matters more than
  compactness there.
- **"?" tooltips** — a round pastel icon with text in a tooltip (`HelpIcon.cs`) instead of
  permanent gray text.
- **Banner** — on the right side of the very top panel, spanning its full height (not a
  separate block above, and not squeezed to match the controls' height), clicking opens
  "About" (same as the footer icon).
- Double-click a record to edit; right-click for check/edit/delete (plus "create folder" on
  the record tree).
- Collapsible output block without losing accumulated text.

## "Zones" tab

`Get-DnsServerZone`, `Add-DnsServerPrimaryZone`, `Remove-DnsServerZone`. Types on creation:
AD-domain / AD-forest / file-based (`.dns`). `[AD]`/`[file]` tag in the list. A source strip
below the list (master servers for Secondary zones). Filter, sort, export.

**Double-clicking a zone** jumps straight to its scopes on the "Scopes and Records" tab. The
reload icon runs `dnscmd /ZoneReload`, always locally (ignores the target server).

## "Scopes and Records" tab

**Tree**: top level is the zone's scopes (loaded lazily, on first selection); inside, records
are grouped by compound names into folders (like `dnsmgmt.msc`):
`admin.pro32connect` → folder `pro32connect`, item `admin`. Grouping is visual, from already
loaded data; a folder is only a node with its own child nodes — single-level records don't
turn into extra tree branches. On the right: the current folder's contents (folders sorted
first, like a file explorer). Split position is remembered (`RecordsTreeSplitter`).

**Adding a record** respects the current folder — `test` inside `pro32connect` becomes
`test.pro32connect`. `@` inside a folder means the folder itself. Priority/Weight/Port
fields only show for SRV/MX.

**Creating a "folder"**: right-click a scope/folder → a wildcard `*` record inside a new
subdomain (`*.sales` → IP) — both a real record and a way to make the subdomain show up as
a folder.

**File-based mode** (notepad icon) — a workaround for Secondary zones where normal adding
fails with `WIN32 9611`: edits the scope's `.dns` file directly, always locally, with a
backup and `dnscmd /ZoneReload`. Shows an explicit warning every time.

**Export** to `.txt` — the first line records the date and the **server name** the export
came from (`DnsHelper.ComputerName` if a target server is set, otherwise the local machine's
name), so the file makes sense even without extra context.

**Import** from such a file (the up-arrow icon): folder rows (`📁 name FOLDER N records.`)
are recognized and not imported as records — instead, the app offers to create the matching
subdomain via a wildcard record (with an IP for each, if wanted). There's an "Exclude @
records" checkbox. Import targets the currently selected folder/scope (same as adding a
single record manually). SRV/MX values are parsed back from the export's composite text
(`target:port (priority=..., weight=...)` / `exchange (preference=...)`); if a record from
the file already exists in the scope (by name+type), it asks whether to overwrite or skip,
with a bulk "all" option — after which further conflicts are resolved automatically without
asking again.

**Cmdlets**: A/AAAA/CNAME/PTR/MX use type-specific cmdlets (`Add-DnsServerResourceRecordA`
etc.). NS/TXT/SRV — only the generic `Add-DnsServerResourceRecord -NS/-Txt/-Srv` (no
dedicated cmdlets exist). `@` in the name field means the zone root.

**Editing** — a re-create (add new first, then remove old, so nothing's lost on failure),
except for **CNAME**: DNS won't let a CNAME coexist with another record under the same name
even momentarily, so the order is reversed (delete first, then add, with rollback on
failure). Deletion passes the full record object via `-InputObject`.

**Checking a record** — nslookup (`Resolve-DnsName`, reformatted output) and Ping (with
`-t`, source `-S`, CP866 encoding).

## "Subnets" tab

`Get/Add/Remove-DnsServerClientSubnet`. Name + CIDR.

## "Policies" tab

`Get/Add/Remove-DnsServerQueryResolutionPolicy`. On the `Get-...` result, the subnet is in
`Criteria`, the scope in `Content` (not `ClientSubnet`/`ZoneScope` — those are `Add-...`
parameter names, not `Get-...` property names). Multiple comma-separated subnets (logical
OR).

## Managing a different server remotely

The "Target DNS server" field at the top — every operation runs through `-ComputerName`,
without moving the app itself. If the current account can't connect, an authentication
window opens (login/password), via `New-CimSession -Credential` (the `DnsServer` cmdlets
have no `-Credential` parameter of their own). The session is cached until the target
changes or the app closes.

Transport is plain WinRM: Kerberos (domain) or NTLM via `TrustedHosts` on the client
(non-domain) — the same as `Enter-PSSession`. The password isn't hashed before sending
(Kerberos/NTLM is itself challenge-response; hashing would break authentication), and is
built directly in memory as a `SecureString`. Full security breakdown: `SECURITY.md`.

If the client isn't domain-joined:
```powershell
Set-Item WSMan:\localhost\Client\TrustedHosts -Value "<server>" -Concatenate -Force
```

The last 10 servers are remembered in `settings.ini`.

## Delete confirmations

A record — plain yes/no. A zone/scope/subnet/policy — a stronger dialog with a 5-second
delay on the "Delete" button.

## Other

- **Auto-loading** of zone/scope lists on first tab visit.
- **`changes.log`** next to the exe — only real changes and auth attempts, no password.
- **Error diagnostics**: full unwrapping of CIM/PowerShell exceptions; clear messages
  instead of raw dumps for common cases — "not a DNS server" (`WIN32 1722`), "administrator
  rights needed" (see above), "WinRM unreachable" (TrustedHosts/firewall/network profile).
- **Updates**: a button in "About" pulls the latest GitHub release, compares versions
  (`AppVersion.cs`), downloads and installs via a `.bat` script (waits for the process to
  exit via `tasklist`, copies with `robocopy`, restarts). Doesn't touch
  `settings.ini`/`changes.log`/`*.dns`. Needs outbound HTTPS to `github.com`.

## How to open and build

1. Copy the folder to the server, open `DnsToolWinForms.csproj` in Visual Studio (2019/2022).
2. NuGet restores automatically. Switch to `Release`, build (`Ctrl+Shift+B`).
3. The finished `.exe` is in `bin\Release\net48\`. Copy it to the server as-is — nothing
   else to install (the PowerShell assembly comes from Windows itself).

**Antivirus flags the exe** — expected for an unsigned exe hosting PowerShell: sign it
(`signtool.exe`), add it to exceptions, or submit it as a false positive.

**NuGet can't find `Microsoft.PowerShell.5.1.ReferenceAssemblies`** — there's a
commented-out `HintPath` block in `.csproj` pointing at the system assembly; uncomment it
instead of the `PackageReference`.

## Structure

- `Program.cs` — entry point. `MainForm.cs` — the whole form (layout built in code, no
  .resx).
- `DnsHelper.cs` — a PowerShell wrapper: cmdlet calls, error parsing, the `CimSession` for
  remote auth, elevation checks.
- `IconFactory.cs`/`HelpIcon.cs` — icons and tooltips (GDI+).
- `*Dialog.cs` — all the dialog windows (zone/scope/record/subnet/policy creation, auth,
  delete confirmation, record check, "About").
- `UpdateChecker.cs` — GitHub-based updates. `FileLogger.cs` — `changes.log`.
  `AppVersion.cs` — the app's version.
- `SECURITY.md` — a breakdown of remote-connection security, with code references.

## Things you could add yourself

Viewing records outside a scope; stricter input validation (IP/CIDR).

## License

MIT — use it, fork it, change it however you like, keeping the attribution (`LICENSE`). No
warranties: this tool writes directly to your DNS configuration — test on a non-critical
environment.

## Support the author

- **USDT (TRC20/TRON):** `TQp9az9Nbnojg65qwvRjhwRkEnwEfFHK77`
- **ETH (ERC20):** `0x40d1775df43a9ff67aabe21ccb000421c0d6f092`

⚠️ Double-check the network before sending. Optional — the tool is free either way ^_^
