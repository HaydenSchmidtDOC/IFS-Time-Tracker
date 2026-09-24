# Security Policy

## Reporting a vulnerability

This is a small internal tool, but it ships an executable that users run and that
auto-updates from GitHub Releases — so a compromised release is a real supply-chain risk.

If you find a security issue, **do not open a public issue**. Report it privately:

- **GitHub private vulnerability reporting**: use the repo's
  *Security → Report a vulnerability* flow (preferred).
- **Email**: contact the repository owner directly.

Please include:
- What the issue is and its impact.
- Steps to reproduce (or a minimal proof of concept).
- Affected version(s), if known.

## What we consider in scope

- Anything that could let an attacker publish a malicious release or modify the
  auto-update metadata (`latest.json`) that the app trusts.
- Code execution, privilege escalation, or data exfiltration in the shipped exe.
- Secrets or credentials accidentally committed to the repo.

## Out of scope

- The app's own time-tracking data (it's local, in a `data/` folder next to the exe).
- General feature requests or non-security bugs — use Issues for those.

## Response

We'll acknowledge reports promptly and work to fix and release a patched version.
Because the app auto-updates, a fix can be pushed to all users via a new GitHub Release.
