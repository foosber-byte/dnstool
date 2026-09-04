[Русская версия](README.md)

# DnsToolWinForms

A WinForms app for managing a Windows DNS Server — covers what `dnsmgmt.msc` doesn't:
records inside a Zone Scope shown as a tree, client subnets, Query Resolution Policies,
remote management of another server with its own authentication, self-updating from GitHub.

Works through the built-in `DnsServer` PowerShell module. Author: foosber, 2026. v2.8.1.

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

- **Icons instead of text buttons** on all 3 tabs — drawn in-house via GDI+
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

## "Scopes and Records" tab (zones, scopes and records — all in one tree)

There's no separate "Zones" tab anymore — zone management moved here, and the zone list
itself became one of the tree's levels. Two clearly bordered blocks, sitting **side by side
horizontally** (not stacked — saves vertical space for the tree and record list below), each
sized to fit its own content — **"Zone management"** (create/delete/reload/export/refresh
the list) and **"Scope/record management"** (create/delete a scope, refresh the current
scope's records) — so it's obvious which button does what, now that everything lives on one
tab.

**Tree**: the top level is servers ("Local" + any server successfully connected to via the
"Target DNS server" panel); inside each server — **"Forward Lookup Zones"**, **"Reverse
Lookup Zones"** and (when present) **"Other zones (conditional forwarders, stub)"**. The
category comes from the zone object itself: `ZoneType` `Forwarder`/`Stub` → "other";
`IsReverseLookupZone` → "reverse" (falling back to the `.in-addr.arpa`/`.ip6.arpa` suffix);
everything else → "forward". Auto-created service zones (`TrustAnchors`, root hints `.`,
`0/127/255.in-addr.arpa`) are hidden — like the normal, non-"advanced", `dnsmgmt.msc` view.
Conditional forwarder and stub zones are leaves (they don't support Zone Scopes,
`WIN32 9603`); clicking one only shows its source. Inside each
category — the zones themselves; inside a zone — its scopes; inside a scope — records
grouped by compound names into folders (like `dnsmgmt.msc`): `admin.pro32connect` → folder
`pro32connect`, item `admin`. Every level loads lazily (on first selection/expansion of its
node) — nothing is pulled from the server needlessly. Grouping records into folders is
visual, from already loaded data; a folder is only a node with its own child nodes —
single-level records don't turn into extra tree branches. On the right: the current
folder's contents (folders sorted first, like a file explorer). Split position is
remembered (`RecordsTreeSplitter`). The **server node** in the tree is highlighted (blue
background, white bold text) — the top level is immediately obvious, not confused with
zones/scopes on the tab.

**Zone management**: creation (`Add-DnsServerPrimaryZone`, types AD-domain/AD-forest/
file-based), deletion (`Remove-DnsServerZone`), reload from disk (an icon with an "R" inside
it — `dnscmd /ZoneReload`, always locally, ignores the target server) — all three apply to
the zone **currently selected in the tree**. Export — the zone names of the current
(expanded) server to a `.txt` file. The line below the two blocks shows the selected zone's
source with mixed formatting (not just plain gray text): the words "Source" and "master
servers" are underlined, the zone type (Primary/Secondary/Stub) and the actual master
server addresses/file path are bold.

**Multiple servers at once**: a connection to a server (see "Managing a different server
remotely" below) is cached for the app's entire lifetime, and this is true for **several**
servers in parallel — switching between them in the tree doesn't drop the connection to the
others. Clicking a server node in the tree switches the "Target DNS server" panel at the top
to it.

**Adding a record** respects the current folder — `test` inside `pro32connect` becomes
`test.pro32connect`. `@` inside a folder means the folder itself. Priority/Weight/Port
fields only show for SRV/MX.

**Creating a "folder"**: right-click a scope/folder → a dialog with a choice of mode. By
default — a wildcard `*` record inside a new subdomain (`*.sales` → IP): both a real record
and a way to make the subdomain show up as a folder (a node only becomes a folder if it has
child nodes of its own — a bare `sales` record with nothing nested under it stays a plain
row, not a folder). The second option is literally like "New Domain" in `dnsmgmt.msc` (a
record named exactly like the folder, no `*`) — with an explicit warning: that way the
folder **won't show up** in the tree until there's another record nested inside it.

**File-based mode** (notepad icon) — a workaround for Secondary zones where normal adding
fails with `WIN32 9611`: edits the scope's `.dns` file directly, always locally, with a
backup and `dnscmd /ZoneReload`. Shows an explicit warning every time.

**Export** to `.txt` — the server name the export came from (`DnsHelper.ComputerName` if a
target server is set, otherwise the local machine's name) is included both in the **file
name** and as the first line inside the file alongside the date, so the file makes sense
even without extra context.

**Import** from such a file (the up-arrow icon): folder rows (`[FLDR] name N records.`)
are recognized and not imported as records — instead, the app offers to create the matching
subdomain via a wildcard record (with an IP for each, if wanted). There's an "Exclude @
records" checkbox. Import targets the currently selected folder/scope (same as adding a
single record manually). SRV/MX values are parsed back from the export's composite text
(`target:port (priority=..., weight=...)` / `exchange (preference=...)`); if a record from
the file already exists in the scope (by name+type), the conflict dialog shows **both the
existing and the new value side by side** (immediately clear whether it's an exact duplicate
or genuinely different IP/name), and asks whether to overwrite or skip, with a bulk "all"
option — after which further conflicts are resolved automatically without asking again. The
"what's already in the scope" list is refreshed as the import proceeds (not just once at the
start) — if the file itself contains a duplicate record, the second occurrence is also
recognized as a conflict against what was just added in the same run, instead of hitting the
DNS server directly and failing with "record already exists".

**Cmdlets**: A/AAAA/CNAME/PTR/MX use type-specific cmdlets (`Add-DnsServerResourceRecordA`
etc.). NS/TXT/SRV — only the generic `Add-DnsServerResourceRecord -NS/-Txt/-Srv` (no
dedicated cmdlets exist). `@` in the name field means the zone root.

**Editing** — a re-create (add new first, then remove old, so nothing's lost on failure),
except for **CNAME**: DNS won't let a CNAME coexist with another record under the same name
even momentarily, so the order is reversed (delete first, then add, with rollback on
failure). Deletion passes the full record object via `-InputObject`; you can select
**multiple** records at once (plain multi-selection in the list, right-clicking inside an
existing selection doesn't clear it) — one confirmation for the whole batch, folders among
the selection are silently skipped.

**Operation progress**: every successful add/delete/import of a record gets its own line in
the output block, `OK: record "name" (type) value added to zone "...", scope "..."` (same
for deletes), rather than just a final summary.

**Checking a record** — nslookup (`Resolve-DnsName`, reformatted output) and Ping (with
`-t`, source `-S`, CP866 encoding).

## "Subnets" tab

`Get/Add/Remove-DnsServerClientSubnet`. Name + CIDR.

## "Policies" tab

`Get/Add/Remove-DnsServerQueryResolutionPolicy`. On the `Get-...` result, the subnet is in
`Criteria`, the scope in `Content` (not `ClientSubnet`/`ZoneScope` — those are `Add-...`
parameter names, not `Get-...` property names). Multiple comma-separated subnets (logical
OR).

Subnets in a policy's description are shown as **names only**, without the CIDR in
parentheses — it used to show the actual range next to the name (`net_100 (10.0.1.0/24)`),
but that "(...)" isn't part of the subnet's actual name, and pasting a line like that into
the "Subnets" field of the new-policy dialog produced an error ("no such subnet exists").
The CIDR is still visible — on the "Subnets" tab.

**Important, easy to forget**: policies (like client subnets) **don't replicate** to backup
domain controllers — they're tied locally to this server/zone. So the same policy name on
**different** zones isn't a conflict (the policy-creation dialog says so explicitly).

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
  rights needed" (see above), "WinRM unreachable" (TrustedHosts/firewall/network profile),
  "zone doesn't support Zone Scopes" (`WIN32 9603` — conditional forwarder zones and similar
  types structurally don't support scopes, it's not a temporary issue).
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

- Viewing records outside a scope; stricter input validation (IP/CIDR).
- Creating a conditional forwarder zone with the same options as `dnsmgmt.msc`
  (`Add-DnsServerConditionalForwarderZone`) — right now the "Zone management" block can only
  create a Primary zone (AD-integrated or file-based); conditional forwarder zones don't
  support Zone Scopes at all (see the `WIN32 9603` diagnostic above), so this would be a
  separate, self-contained branch of functionality, not overlapping with the rest of the app.

## License

MIT — use it, fork it, change it however you like, keeping the attribution (`LICENSE`). No
warranties: this tool writes directly to your DNS configuration — test on a non-critical
environment.

## Support the author

- **USDT (TRC20/TRON):** `TQp9az9Nbnojg65qwvRjhwRkEnwEfFHK77`
- **ETH (ERC20):** `0x40d1775df43a9ff67aabe21ccb000421c0d6f092`

⚠️ Double-check the network before sending. Optional — the tool is free either way ^_^
