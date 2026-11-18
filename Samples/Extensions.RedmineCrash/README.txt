Redmine crash-report provider sample — SDK API 1, net10.0, Windows secret store

This is trusted arbitrary .NET code, NOT a sandbox. Review it before enabling.
The source depends only on LiberaFx.Extensions.Abstractions + .NET BCL, not the editor.
No HTTP call occurs during registration; configuration is read from the package.

Build Extensions.RedmineCrash.csproj independently. It targets net10.0 and
references only LiberaFx.Extensions.Abstractions. Its output assembly is
LiberaFx.Sample.RedmineCrash.dll; the manifest is copied beside it.
Do not add an editor project reference. Do not copy the SDK assembly into a private
alternate load context: the shared host supplies that contract assembly.

Create redmine.json in the output/package directory (not source control), e.g.:
  {"BaseUrl":"https://your-tracker.example/redmine/","ProjectId":123,
   "CredentialTarget":"LiberaFX.Redmine"}
No token belongs in that JSON. In Windows Credential Manager create a generic
credential with the matching target, arbitrary username, API key as password.
Token is read only when the user explicitly checks the destination or submits.
No environment/config token fallback. Other OS stores are explicitly unsupported.

Install the independently built DLL, liberafx-extension.json and redmine.json in
one child directory of AppDataDirectory.Root/LiberaFx/Extensions. Enable it only
after trusting the package in shared host Settings, then restart as requested by
the host. Changing configuration changes its fingerprint and requires reapproval.
Disable via shared host Settings. There is no second loader or preferences file.

Redmine compatibility limits:
- HTTPS only; redirects are rejected; private/local-network endpoints are the
  user's explicit choice in this trusted extension's config.
- Requires standard REST API and project GET returning id + is_public.
  Missing visibility fails closed, not "probably private".
- POST /issues.json uses the project's default tracker. Required custom fields,
  tracker-specific forms and SSO are not implemented; such requests fail visibly.
- No attachment upload is implemented. A selected attachment rejects the ENTIRE
  report BEFORE any request rather than silently dropping it.
- REST project visibility is not a full audit of Redmine roles, reverse proxies
  or third-party access. User must verify access policy separately.
- No response bodies or provider exception details are shown by the report UI.
- No automatic retry, discovery of another endpoint, or alternate authentication.

Compilation alone does not establish host loading/configuration/error behavior
or service compatibility. Real submissions require explicit permission and a
dedicated test tracker. No external service is contacted during compilation.
