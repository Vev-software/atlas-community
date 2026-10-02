# Pushing the landscape digest to a connected consumer

A customer can keep a **consenting consumer** up to date with the [landscape digest](./DEVELOPMENT.md#sharing-a-summary-the-landscape-digest-file)
instead of handing over files. Atlas **pushes outbound over HTTPS**; the consumer never pulls, so the customer opens no inbound
port and runs no VPN. The same path serves a self-hosted and a hosted Atlas, so it is reviewed and tested once.

It is **off by default**. Nothing is pushed until an admin connects a consumer, and Atlas contacts only the one address that admin
entered.

## Connecting

Under **Connected consumers** (author role, the same `atlas.landscape.share` authorization as the file), an admin enters the
consumer's address, the **one-time activation code** the consumer showed, and what to share (kinds and optional tags, with the same
preview as the file). Atlas redeems the code at the consumer and stores the **credential** it gets back protected with the
application's data protection keys. The credential is never shown again and is never part of any API reply.

The code and the data-sharing enrollment it belongs to are defined by the public Fabric data-sharing enrollment contract: a
tenant-bound credential that binds one source tenant to one consumer account, with a single-use, expiring activation code, and that either
side can revoke.

## What Atlas calls

Only these three calls, only to the destination the admin entered (`{base}`), and never with any content except the signed digest:

| Call | Body | Reply |
|---|---|---|
| `POST {base}/activate` | `{ "activationCode", "keyId", "publicKey" }`: the code, and the key that will sign the digests so the consumer can pin it | `200 { "enrollmentId", "credential", "credentialExpiresAt" }`, or an error with `{ "reasonCode" }` |
| `POST {base}/digests` | the signed digest (`{ "digest", "signature" }`), `Authorization: Bearer <credential>` | `2xx` accepted; `401`/`403` with `{ "reasonCode" }` refused; `409` replayed sequence; `408`, `425`, `429`, `5xx` try later |
| `POST {base}/revoke` | none, `Authorization: Bearer <credential>` | any `2xx` |

Reason codes are Fabric's (`sharing_enrollment_revoked`, `sharing_enrollment_suspended`, `sharing_credential_expired`,
`sharing_binding_mismatch`, `sharing_activation_code_invalid|expired|used`).

## When a push happens

- **After a change**, debounced: a push follows the last edit of a burst by `Atlas:Share:Push:DebounceSeconds` (default 60), so a burst of
  edits is one push. A change that does not alter what the consumer would see is not pushed.
- **At least daily** (`DailyHours`, default 24), changed or not, so the consumer can tell the sharing is alive.
- **Right away** after connecting, and on request (**Push now**).
- Each push is a fresh digest with a higher `sequence`, signed with the installation's key. The first round after startup comes one
  interval later, never in the moment the installation starts.

## When it goes wrong

- A transient failure (unreachable, throttled, `5xx`) is **retried with growing waits**: one minute, doubling, up to `MaxBackoffMinutes`
  (default 360). The status shows the last error and the next attempt. Only the kind of failure is kept: no payload, address query or
  message from the consumer.
- A consumer that **refuses** (it revoked or suspended the enrollment, the credential expired, or the digest is not for the account it
  connected) **stops** the pushes. Atlas shows why, forgets the credential, and does not retry. Connecting again needs a new code.
- **Pause** (this side) stops pushes and can be resumed. **Revoke** (this side) stops them at once, forgets the credential, and tells the
  consumer on a best-effort basis; the local revocation stands even if the consumer cannot be reached. A revoked or stopped consumer is
  never pushed to, not even on request.
- Changing the scope takes effect on the next push.

## Hardening

- The destination must be a plain `https://` address without credentials, query or fragment; plain `http` only when
  `AllowInsecureHttp` is set (development). `AllowedHosts` limits an installation to listed hosts.
- The transport follows **no redirects** and checks **every address it connects to**: a destination that resolves to a loopback, private,
  link-local, unique-local or carrier-grade-NAT address is refused unless the operator set `AllowPrivateDestinations`. This is what keeps a
  hosted installation from being used to reach into its own network.
- At most `MaxConsumersPerTenant` (default 5) consumers per tenant; a destination can be connected once.
- Connecting and pushing on request share the per-tenant rate limit of the file digest (`Atlas:Share:PermitLimit`).
- **Audit**: exactly one record per push (`atlas.landscape.share.push.accept`, `.deny` when the consumer refused, `.fail` for a transient
  failure), with counts and the sequence only; connecting, pausing and revoking are audited too (`fabric.sharing.enrollment.*`). No payload
  content is written to logs or the audit trail.
- Pushes run in the background under a system identity that exists only for this, inside the consumer's own tenant.

## Settings

`Atlas:Share:Push:` `PollSeconds` (30), `DebounceSeconds` (60), `DailyHours` (24), `MaxBackoffMinutes` (360), `MaxConsumersPerTenant` (5),
`AllowPrivateDestinations` (false), `AllowInsecureHttp` (false), `AllowedHosts` (empty).
