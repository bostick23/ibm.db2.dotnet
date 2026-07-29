# Security policy

## Supported versions

Db2i is pre-alpha software. Security fixes are provided only on the latest
`0.5.x` pre-release line and the current `main` branch.

| Version | Supported |
| --- | --- |
| 0.5.x pre-releases | Yes |
| Earlier versions | No |

## Reporting a vulnerability

Do not disclose vulnerabilities in public issues, discussions, pull requests,
logs, or test fixtures. Use
[GitHub private vulnerability reporting](https://github.com/bostick23/ibm.db2.dotnet/security/advisories/new)
and include:

- the affected version and runtime;
- a minimal, sanitized reproduction;
- the security impact and required preconditions;
- any suggested mitigation.

Never include live credentials, connection strings, host names, IBM i job
identifiers, or private database contents. The maintainers will acknowledge a
report as soon as practical, investigate it privately, and coordinate disclosure
after a fix or mitigation is available.
