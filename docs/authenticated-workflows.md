# Authenticated catalogue workflows

OIDC remains the default outside Development. Configure `Atlas:Identity:Oidc:Authority`
to the canonical token issuer, `Audience` to the API audience and `BrowserAuthority`
to an address reachable by users. Optional `MetadataAddress` lets the server retrieve
discovery over an internal network without changing the validated issuer. Internal
HTTP metadata needs explicit `RequireHttpsMetadata=false`; browser traffic should use HTTPS.

An optional `Atlas:Identity:AllowedTenant` restricts both user and machine requests to
one tenant. Requests without verified tenant identity remain denied. Tenant headers
never override signed claims.

To admit a machine alongside OIDC users, explicitly set
`Atlas:Identity:ServiceAssertion:Enabled=true` and configure `PublicKeyPem`, `KeyId`,
`Issuer`, `Audience` and `Roles` under that section. This uses the existing public
Fabric signed service-identity contract. The assertion branch takes precedence when
its header is present; an invalid assertion cannot fall back to a valid user token.
Leave the option unset to retain OIDC-only behavior. Store keys outside source control.

`Atlas:DataProtection:KeyPath` persists the keyring. Back it up with the SQLite
catalogue and protect the storage with host permissions and disk encryption.

The browser sends its bearer token on session, account and catalogue requests.
Sign out revokes the refresh session at the configured provider and clears the local
tokens. Already issued access tokens expire normally; logout is not immediate token
revocation. This release retains the existing direct-password-grant login; federated
SSO/MFA requires a future authorization-code flow.

Same-origin fragment documents are fetched with bearer authentication and mounted as
`srcdoc` in the existing empty sandbox. Redirects are refused and tokens are never
forwarded to an external fragment origin. External fragments keep their own
authentication. Fragments must be self-contained documents; the V1 mount schema and
server entitlement decisions are unchanged.

Manual relationships now support `PUT /api/v1/relationships/{id}` with the existing
public relationship JSON shape. The identifier must match the path, both endpoints
must exist in the current tenant, and write authorization is required. Missing
relationships return 404, invalid endpoints/id return 400, and denied writes return
403. The UI exposes Edit beside each editable relationship. This is additive;
existing create/delete behavior and published model packages are unchanged.
