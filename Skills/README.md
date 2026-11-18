# Bundled operator skill

`liberafx-operator/SKILL.md` teaches an AI client to operate LIBERA FX through
MCP. It is not a source-code development skill. The editor build and publish
outputs include this directory so instructions travel with the matching API.
Keep the skill in English; conversations may use the user's preferred language.

## Install or update

In the editor's **MCP connection** window:

1. Copy the bundled skill path if the AI client supports adding a skill file.
2. Alternatively, enter an absolute **skills directory** supported by your
   client. The installer creates its `liberafx-operator` subdirectory; do not
   enter that final subdirectory yourself.
3. Click **Install / update**. Existing identical content is left unchanged.
   Different existing content is not replaced unless you explicitly enable
   replacement. Replacement keeps the previous `SKILL.md` in a uniquely named
   backup in the same directory.
4. Reload skills or restart the AI client as required by that client.

For Copilot CLI, project skill directories include `.github/skills`,
`.agents/skills` and `.claude/skills`. Its documented file installation command
is also available, for example:

```powershell
copilot skill add --project "C:\Apps\LiberaFx\Skills\liberafx-operator\SKILL.md"
```

For another client, use its documented skills directory or skill import
workflow; not every client automatically discovers the same locations.
The editor installer writes only this skill, never client settings, MCP
credentials or unrelated skills. Nothing is installed into a user profile
automatically. After upgrading the editor, explicitly update the installed copy.

Configure the authenticated MCP connection separately using the endpoint and
token displayed by the editor. Do not put credentials in any skill file.
