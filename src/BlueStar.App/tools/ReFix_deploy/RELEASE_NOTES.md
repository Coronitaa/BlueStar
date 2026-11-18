# ReFix 2.5.4

## ReFix Online + LAN is now ReFix Mixed
- The recommended product keeps working the same way: LAN play with no Steam client, plus optional Steam friends from the overlay. Only its name changes, to **ReFix Mixed (LAN + Online)**, in AutoDeploy and the readme. Folders, mode ids and `REFIX_DEPLOY_MODE=1` are unchanged, so scripts and launchers need no update.

## Linux (Wine / Proton)
- **Overlay icons.** Wine and Proton prefixes don't have Windows' Segoe icon fonts, so every overlay icon showed up as "?". ReFix now carries its own icons (from Microsoft's Fluent System Icons, MIT licensed) and uses them when the system fonts are missing. That covers the profile avatars too.
- **Import your Steam account under Proton.** The profile import reads the account from the host's Steam install instead of the Wine registry, and turns the Steam link on if it's off. Thanks @Syntist (#34).

## Fixes
- On Windows, the profile avatars now pick their icon font by the font file that's installed. Before, the fallback to Segoe MDL2 Assets for Windows 10 could never trigger.

Not tested in a Linux game yet. The icon font was checked under Wine 9: every icon the overlay uses is found, both in the overlay and for the avatars.
