# Security policy

## Reporting a vulnerability

Please report security issues privately rather than opening a public issue. Use GitHub's
[private vulnerability reporting](https://github.com/HarryCordewener/ladybugdb-dotnet-client/security/advisories/new)
for this repository if it's available on the Security tab. If it isn't, contact the maintainer
([@HarryCordewener](https://github.com/HarryCordewener)) directly rather than filing a public
issue with exploit details.

Include what you'd include for any vulnerability report: affected version, a reproduction, and
the impact as you understand it. This is a young, unpublished project (see the README's
[status note](README.md#current-status-and-limitations)) — response times are best-effort, not
covered by an SLA.

## Scope

**In scope:** this repository — the .NET binding itself (`LadybugDb.Client`), how it marshals
data across the P/Invoke boundary, how it manages native handle lifetime, and how
`LadybugDb.Client.Native` packages and verifies the binaries it redistributes.

**Out of scope:** the LadybugDB engine itself. A vulnerability in `liblbug`'s query execution,
storage format, or C API belongs to [LadybugDB/ladybug](https://github.com/LadybugDB/ladybug) —
please report it there. If you're not sure which side of the boundary a bug is on, report it here
and we'll redirect it if needed.

## Native binary integrity

`LadybugDb.Client.Native` redistributes prebuilt `liblbug` binaries. It never builds them from
source. Every binary is pinned to a specific upstream release and verified by SHA256 against
`LadybugDb.Client.Native/liblbug.lock` before it's placed in the package — see
[docs/BUILDING.md](docs/BUILDING.md#how-native-binaries-are-pinned-and-verified). A hash mismatch
fails the build rather than shipping an unverified binary.
