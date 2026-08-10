# AGENTS.md — repo-local guardrails for public disclosure

This repository is public. Read `Vev-software/engineering/AGENTS.md` for the full policy; this
file narrows it to the disclosure risks that matter most here.

## Public disclosure rules

- Public PR titles/bodies, issue bodies, README/docs, ADRs, and `.github` templates must describe
  only:
  - code and behaviour in `atlas-community`
  - published public contracts and public-facing boundaries
- Do **not** include:
  - private repo names, private module names, or private pipeline details
  - proprietary deployment topology or control paths
  - licence enforcement or entitlement-verification detail
  - trial mechanics, internal hostnames, customer names, or security-control specifics
  - secrets, credentials, tokens, or customer data

## Escalation

- Security vulnerabilities do **not** belong in a public issue or PR. Follow `SECURITY.md`.
- If the explanation seems to require private implementation detail, move that detail to a private
  channel and leave only the public-safe summary here.
