[Русская версия](README.md)

# DnsToolWinForms

A simple WinForms app for managing a Windows DNS Server — covering what the built-in
`dnsmgmt.msc` snap-in doesn't: zones (AD-integrated and file-based), Zone Scopes, records
inside a scope (A/AAAA/CNAME/PTR/TXT/SRV), client subnets, and Query Resolution Policies.

It works through the built-in `DnsServer` PowerShell module — the app must run **on the DNS
server itself**, as administrator.

Author: foosber, 2026.

## Requirements

- **.NET Framework 4.8** (the project's target framework is `net48`, see
  `DnsToolWinForms.csproj`). It's already installed out of the box on most Windows Server
  versions (2016+); if not, it can be added via
  `Programs and Features → Turn Windows features on or off`, or downloaded separately from
  Microsoft's site.
- **Windows Server with the DNS Server role** — the app works through the built-in
  `DnsServer` PowerShell module, which is only present where that role is installed
  (or via RSAT: DNS Server Tools on a client machine, for remote management).
- **Local administrator rights** to run the app (mandatory — see `app.manifest`).
- **Visual Studio 2019/2022** — only needed to build from source (not required if you're
  just using a pre-built `.exe` from Releases).

## Editing, checking and deleting a record

All actions on a specific record ("Scopes and records" tab) live in one place — a
**right-click context menu** on the record in the list. There are no separate buttons for
this on the toolbar anymore, on purpose, to avoid a button for every single action.

- **Double-click** on a record (or the **"Edit record..."** menu item) opens the edit
  dialog: type, name, value (+ Priority/Weight/Port for SRV).
  The `DnsServer` module has no reliable "edit a record in place" command that works for
  all types, so under the hood this is a **re-create**: the new record is added first with
  the new values, and only on success is the old one removed. If adding fails, the old
  record is left untouched — nothing is lost.
- **"Delete record..."** in the same menu — a plain yes/no confirmation (no 5-second
  countdown; that's reserved for heavier operations like deleting an entire zone/scope/
  subnet/policy).
- **"Check record..."** in the same menu opens a window with two checks:
  - **nslookup** (via `Resolve-DnsName`, but the output is reformatted to look like the
    familiar console nslookup, without CIM-object clutter). The name field is pre-filled
    (already with the zone's domain) and editable. The server field — if empty, uses the
    app's current target server (see "Target DNS server" below); if that's empty too, the
    local resolver is used.
  - **Ping** — plain ICMP via `ping.exe`, 4 packets by default (like classic ping) or
    continuous (the `-t` checkbox, with a separate "Stop" button). If the "Server" field is
    filled in, it's passed as `-S` (source address) — useful on a multi-homed machine (e.g.
    comparing the internal address vs. a NAT address). This is **not** "run ping from
    another server" — Windows has no Linux-style `-I`; `-S` only works with addresses that
    actually belong to this machine.
  - `ping.exe` output on Russian Windows builds is written in the CP866 console codepage
    (otherwise you'd get garbled text) — this is handled when reading the output.

## Auto-loading zone lists

The zone list ("Zones" tab) and the zone dropdowns ("Scopes and records" and "Policies"
tabs) load automatically the first time you open the tab — no need to click "Refresh" on an
empty list. If the list is already populated (loaded earlier or refreshed manually), the app
won't re-hit the server on every tab switch.

## File-based mode for Secondary zones (workaround)

If the zone on this server is **Secondary** (a read-only replica) but the scope you need
only exists locally (i.e. it isn't replicated via zone transfer from the master — see the
troubleshooting story below for the specific case that led to this), the regular "Add
record" button won't work: the DNS Server API refuses with `WIN32 9611 / "DNS zone type is
not allowed"`, because the zone is read-only.

The **"Add record (file, for Secondary zones)"** button under the main record form is a
workaround for exactly this case:
1. The line is appended **directly to the scope's `.dns` file** at
   `C:\Windows\System32\dns\<zone>\<scope>.dns` — **always locally**, on this same machine,
   regardless of the "Target server" setting above (the whole point is that the physical
   file lives right here).
2. A backup of the file is made automatically before editing (a timestamped copy next to
   it).
3. The zone is reloaded via `dnscmd /ZoneReload <zone>` — this re-reads the file from disk
   without asking whether the zone is Primary or Secondary (unlike the record-add API).

This bypasses DNS Server's normal checks, so the button shows an explicit warning every
time it's clicked. Use it only when you fully understand what you're doing — and only after
confirming the scope doesn't exist on the master (otherwise it's an "orphaned" local
artifact, not a real replica — see the "Zone source" section below).

## Collapsing the output panel

The "▲ Collapse" / "▼ Show" button next to "Clear" collapses the output block at the bottom
of the window down to a single row of buttons, without losing the accumulated text (it just
hides it, doesn't clear it) — handy when the output list gets long and you don't want to
keep scrolling past it.

## Version and icon

The app version is baked into `AssemblyTitle` (in `DnsToolWinForms.csproj`) and into the
main window's title, in the format `v1.9.<N>` — the number increases by 1 with every change,
so a glance at the window/exe tells you exactly which build is deployed.

The icon (`<ApplicationIcon>icon.ico</ApplicationIcon>` in the csproj) is used both for the
exe and in the title bar of **every** window in the app (main window and all dialogs —
delete confirmations, record editing, record checking) via the shared `AppIcon.cs` helper.
**The `icon.ico` file must actually exist next to the `.csproj`** — without it the build
fails (MSBuild can't find the file for the Win32 resource).

## How to open and build

1. Copy the entire `DnsToolWinForms` folder onto the DNS server.
2. Open `DnsToolWinForms.csproj` in Visual Studio (2019/2022, SDK-style csproj support
   required).
3. Restore NuGet packages (Visual Studio does this automatically on open).
4. To get a finished `.exe`, switch the configuration dropdown from `Debug` to `Release` and
   build (`Build → Build Solution`, `Ctrl+Shift+B`). The file will appear at
   `bin\Release\net48\DnsToolWinForms.exe`.
5. Run it — the app will request administrator rights (UAC) itself; this is mandatory,
   otherwise the DnsServer cmdlets will return Access Denied.

The `System.Management.Automation` reference the project uses is only needed for
**compilation** (pulled from a reference-assemblies package) and isn't copied next to the
exe — at runtime the app finds the real PowerShell assembly already present in Windows. So
the `.exe` can simply be copied to a server — nothing else needs to be installed.

### If Windows Defender / your antivirus flags the exe

This is expected for an unsigned exe that hosts PowerShell internally and requests admin
rights — classic heuristic triggers. Real options:
- sign the exe with a corporate code-signing certificate (`signtool.exe`);
- add the exe/folder to your antivirus's exceptions on the server itself;
- submit it for review as a false positive (for Kaspersky —
  https://opentip.kaspersky.com/).

## If NuGet can't find the `Microsoft.PowerShell.5.1.ReferenceAssemblies` package

`DnsToolWinForms.csproj` contains a commented-out block with a direct `HintPath` reference
to the local PowerShell 5.1 system assembly. Uncomment it and remove the `PackageReference`
above it — details are right there in the csproj file.

## Structure

- `Program.cs` — entry point; this is also where the change log file is created on startup.
- `MainForm.cs` — the whole form: 4 tabs + a shared output block at the bottom + a footer.
  The layout is built in code (no .resx/Designer.cs) via simple `Row`/`Column`
  FlowLayoutPanel helpers.
- `DnsHelper.cs` — a wrapper around `System.Management.Automation`: calls cmdlets via
  `AddCommand`/`AddParameter` (no string concatenation), flattens nested/array property
  values into readable text, and extracts the real error text from `InnerException`/CIM
  fields.
- `FileLogger.cs` — writes the `changes.log` file next to the exe.

## Tabs

### Zones
`Get-DnsServerZone`, `Add-DnsServerPrimaryZone`, `Remove-DnsServerZone`.

When creating a new zone you can pick the type:
- **AD-integrated (replication: domain)** — `-ReplicationScope Domain` (the default);
- **AD-integrated (replication: forest)** — `-ReplicationScope Forest`;
- **File-based (.dns on disk)** — `-ZoneFile <name>.dns`, no AD involved.

The zone list shows a short `[AD]` / `[file]` tag next to the name (based on the
`IsDsIntegrated` property), so you can immediately see what you're dealing with.

### Scopes and records
`Get/Add/Remove-DnsServerZoneScope`, plus type-specific cmdlets for adding records:

| Type  | Cmdlet                              | Value parameter    |
|-------|--------------------------------------|---------------------|
| A     | `Add-DnsServerResourceRecordA`      | `-IPv4Address`      |
| AAAA  | `Add-DnsServerResourceRecordAAAA`   | `-IPv6Address`       |
| CNAME | `Add-DnsServerResourceRecordCName`  | `-HostNameAlias`     |
| PTR   | `Add-DnsServerResourceRecordPtr`    | `-PtrDomainName`     |
| TXT   | `Add-DnsServerResourceRecord -Txt`  | `-DescriptiveText`   |
| SRV   | `Add-DnsServerResourceRecord -Srv`  | `-DomainName`        |

Important: A/AAAA/CNAME/PTR use the **type-specific** cmdlets rather than the generic
`Add-DnsServerResourceRecord -A/-AAAA/...` — the generic variant failed with a generic,
detail-free error in a couple of scenarios, and the type-specific cmdlets turned out to be
more reliable.

**TXT and SRV are the exception**: `Add-DnsServerResourceRecordTxt` and
`Add-DnsServerResourceRecordSrv` **do not exist** as standalone cmdlets (verified against
Microsoft's official documentation) — for these two, the generic
`Add-DnsServerResourceRecord` with the `-Txt`/`-Srv` switch is mandatory. Calling the
non-existent type-specific cmdlet raises a `CommandNotFoundException`.

For a record at the zone root (SOA/NS/an SPF record inside TXT, etc.), enter `@` in the name
field.

The zone is picked from a **dropdown** (still editable by hand) — the list loads
automatically on startup; the `↻ zones` button refreshes it manually.

The record list shows **actual values** (IP, target name, text) rather than a .NET type
name — `RecordData` from `Get-DnsServerResourceRecord` is a nested CIM object, and
`DnsHelper.DescribeRecordData()` pulls out the right field per record type (for SRV it also
shows port/priority/weight).

### Special case when editing: switching a record to/from CNAME

DNS does not allow a CNAME to coexist with **any** other record under the same name — not
even momentarily. The normal safe edit order ("add the new record first, then remove the
old one" — so the original isn't lost if something fails) **physically cannot work** in this
case: the server will refuse to add the new record while the old one still exists under that
name (error code `WIN32 9708 "The node is a DNS CNAME record"`).

If the record name isn't changing and either side (old or new) is a CNAME, the app
automatically switches to the reverse order: delete the old record first, then add the new
one. If adding the new record then fails, it attempts an **automatic rollback** (re-adding
the old record) so the name isn't left without any record at all. If the rollback also
fails, this is explicitly logged with a "no record exists, add it manually" note.

Deleting a record passes the **entire object** via `-InputObject`
(`Get-DnsServerResourceRecord | Remove-DnsServerResourceRecord`) — more reliable than
assembling `-RecordData` by hand.

### Subnets
`Get/Add/Remove-DnsServerClientSubnet`. The list shows the name plus the actual CIDR.

### Policies
`Get/Add/Remove-DnsServerQueryResolutionPolicy`.

**An important quirk**, found the hard way: on the object returned by
`Get-DnsServerQueryResolutionPolicy`, the subnet lives in the **`Criteria`** property, and
the scope in **`Content`**. The `ClientSubnet`/`ZoneScope` properties simply don't exist on
the returned object — those are **parameter** names for
`Add-DnsServerQueryResolutionPolicy`, not property names on the `Get-...` result. The module
names its input and output differently.

The policy list shows only names (compact, no line overflow); on the right is a details
panel for the selected policy, with subnets highlighted in green and the scope in blue.
Subnet names from `Criteria` are additionally resolved to their real CIDR (a
`Get-DnsServerClientSubnet` query, matching name → range).

Creating a policy supports multiple comma-separated subnets (logical OR):
```
-ClientSubnet "EQ,net_100,Old_DNS_redirect13,Old_DNS_redirect6"
```

## Managing a different DNS server remotely

By default the app talks to the local DNS service (wherever it's running). But there's a
**"Target DNS server"** field at the top of the window — enter another server's name/IP
there, and **every** operation on every tab starts running against it via `-ComputerName`
(a parameter supported by nearly every cmdlet in the `DnsServer` module) — without moving
the app itself to that server.

Useful, for example, when the zone you need on the current server is `Secondary` (a
read-only replica), and you can only edit it on its `Primary` server (the master). Instead
of copying the exe over there, you can just point this field at the master once.

For this to work:
- **WinRM** must be enabled on the target server (usually already on by default on Windows
  Server; if not, run `Enable-PSRemoting` on it);
- the firewall must allow WinRM (TCP 5985) between your machine and the target server;
- the account running the app must have DNS management rights on the target server (the
  same thing you'd need for local operation) — if it's the same forest/domain and account,
  Kerberos authentication happens transparently, nothing extra to configure.

The "Test connection" button next to the field runs a trial `Get-DnsServerZone` and shows in
the log whether it could reach the server.

Leave the field empty and everything reverts to working with the local server, as before.

## Delete confirmations

Deleting a single record (A/AAAA/CNAME/...) — a plain yes/no window, no extra drama.

Deleting a **zone, scope, subnet, or policy** — a stronger dialog (`DangerConfirmDialog.cs`):
large red text describing the consequences, and the "Delete" button is disabled for the
first 5 seconds with a visible countdown (5...4...3...2...1) — specifically so you can't
click through it on autopilot without reading. "Cancel" is active immediately.

## Draggable-split lists

On the "Scopes and records" and "Policies" tabs, the border between the two panels (list
and details/records) is draggable (`SplitContainer`). The position is saved to
`settings.ini` next to the exe and restored on the next launch (under the
`ScopesRecordsSplitter` and `PoliciesSplitter` keys). If the settings file is unavailable or
corrupted, the default position is used instead — nothing crashes.

## Sorting, filtering, and exporting lists

On the "Zones" and "Scopes and records" (record list) tabs you get:
- **Filter** — a text field; the list narrows as you type (matches against all visible
  fields in the row: name, type, value/tag). Works instantly, with no server round-trip —
  it filters what's already loaded.
- **Sort** — a field dropdown (Name/Type/[Value]) + a ▲/▼ button to flip direction.
- **Export to file...** — saves exactly what's currently shown in the list (i.e. already
  filtered and sorted) to a `.txt` file, with the path chosen via the standard save dialog.
  Also available on the "Subnets" tab.

Important for records within a scope: sorting/filtering changes the on-screen row order, but
doesn't confuse deletion — the app keeps a separate "what's actually shown right now" list
and deletes exactly the record that's selected, not whatever would be at that index in the
original (unsorted) server response.

## Zone source

On the "Zones" tab, under the list, there's a strip showing the source of the selected zone:
- for `Primary` — AD (domain) or file (`.dns`, with the file name);
- for `Secondary`/`Stub` — the list of master servers (`MasterServers`) the zone replicates
  from. This kind of zone can't be edited here at all — you need to edit it on one of the
  listed masters (see the "Target DNS server" section above).

## Remote server history

The "Target DNS server" field remembers the last 10 servers the app successfully connected
to (local doesn't count) — switching from "Local" to manual entry immediately opens a
dropdown with this history, so you don't have to retype the address every time. Stored in
`settings.ini` next to the exe.

## Change log file

`changes.log` lives **next to the exe**, and is created automatically the first time the
app runs (if it already exists, it's left alone and just appended to). Only real changes are
logged (creating/deleting a zone, scope, subnet, policy, record) — no "noise" from list
refreshes. The file size isn't capped.

Line format:
```
2026-07-16 14:04:32 | OK     | RECORD ADD    | zone/object: corp.local | Scope=... A test -> 10.0.0.1 | user: admin
```

The "Open change log file" button in the output panel opens the file in its associated
program (usually Notepad).

## Error diagnostics

- If a PowerShell cmdlet throws an exception, the log gets the **entire** `InnerException`
  chain plus, via reflection, extra fields like `StatusCode`/`NativeErrorCode`/`ErrorData`
  (that's usually where the real cause hides for CIM exceptions, rather than the generic
  "Failed to create resource record..." message).
- If, after querying a policy, the subnet/scope field is empty, the app automatically dumps
  **all real properties** of the object into the log, so you can see the exact property
  names instead of guessing (see the `Criteria`/`Content` story above).

## Things you could add yourself

- Viewing/deleting records outside a scope (directly in the zone itself, with no scope
  binding) — can be added by following the pattern already in place for scope records.
- Support for other record types (NS, MX, etc.) — the pattern already exists in
  `AddRecordToScopeAsync`; just add another `case` with the right cmdlet/parameter.
- Input validation (IP addresses, CIDR) is minimal — format errors get passed straight into
  the output panel from PowerShell/the DNS server as-is.

## License

MIT — use it, fork it, change it however you like, including commercially. The only
condition is keeping the attribution notice (the `LICENSE` file). No warranties: this tool
writes directly to your DNS server's configuration — test it on a non-critical environment
before using it in production.

## Support the author

If this tool was useful, you're welcome to send a bit of crypto:

- **USDT (TRC20 / TRON network):**
  `TQp9az9Nbnojg65qwvRjhwRkEnwEfFHK77`

- **ETH (Ethereum network, ERC20):**
  `0x40d1775df43a9ff67aabe21ccb000421c0d6f092`

⚠️ Double-check the network before sending — a transfer on the wrong network may not arrive.

Totally optional — this tool is free and open regardless of whether you donate or not ^_^
