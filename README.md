# SmokyPluginV2

EXILED 9.14.2 plugin for SCP: Secret Laboratory 14.2.7.

Version 0.10.0 includes an integrated Discord bot, persistent warnings, Steam-to-Discord account linking, Discord-role synchronization, first-time late-join spawning and the restored pink SCP-330 candy. The bot runs inside the plugin process; no NodeJS application, OAuth callback or separate bot executable is required.

## Current features

- Persistent warnings stored in `EXILED/Data/SmokyPluginV2/<port>/warnings.yml` using temporary-file replacement for safe writes.
- Remote Admin commands: `warn`, `warnings`/`warns`, and `delwarn`/`unwarn`/`rmwarn`.
- Player console command `.warns` to view personal warnings without moderator information.
- One `smokyplugin.warnings` Exiled.Permissions node controls all administrative warning commands.
- Warning creation and deletion enforce the Remote Admin hierarchy without exposing its numeric values in responses or logs.
- The YAML file is checked before every warning operation, so valid manual edits are visible without restarting the server.
- Immediate active-round restart after the last player leaves.
- First-time late joiners can spawn as Class D, Facility Guard or Scientist during a configurable opening window; reconnecting participants cannot spawn again.
- After a main MTF or Chaos wave, first-time joiners have a separate 60-second window to spawn as an MTF Private or Chaos Rifleman. Reinforcement mini-waves are ignored.
- SCP-330 can replace a candy drawn from its bowl with the pink candy at a configurable chance (20% by default).
- Classic Discord game-event logs: plain text, timestamps, emoji and one-second batching like the old DiscordIntegration plugin.
- Player join logs include IP addresses by default.
- An individual switch for every game-event type.
- A separate log for every Remote Admin command.
- Separate moderation embeds for kicks, bans, voice/intercom mutes, unmutes and unbans.
- Bot presence: online while players are connected and idle while the server is empty.
- Activity text: `{players} / {max_players} в игре`.
- Discord `+` commands are executed through Remote Admin with permissions from the mapped Discord role.
- Guild slash commands `/link`, `/unlink`, and `/link-status` with private ephemeral responses.
- One-time Steam-to-Discord linking codes, valid for five minutes and stored only in memory.
- Persistent account links stored atomically in `EXILED/Data/SmokyPluginV2/<port>/account-links.yml`.
- Current Discord roles are fetched when a linked player verifies and mapped to a temporary SL Remote Admin group.
- Native groups explicitly assigned in `config_remoteadmin.txt` are preserved by default.

## Installation

Copy only `bin/Release/SmokyPluginV2.dll` to the EXILED plugin directory for the correct server port:

```text
~/.config/EXILED/Plugins/<server-port>
```

For port `25566`, for example:

```text
~/.config/EXILED/Plugins/25566
```

No Discord dependency DLLs are required. The plugin uses framework libraries already available to the game server and EXILED's existing Harmony dependency.

## Discord application setup

1. Create an application and bot at <https://discord.com/developers/applications>.
2. On the **Bot** page, enable **Message Content Intent**. It is needed because `listen_for_commands` is enabled by default.
3. Invite the bot to your Discord server with the `bot` scope. The `applications.commands` scope is included automatically with bot installations.
4. Give it these permissions in all configured log channels:
   - View Channel
   - Send Messages
   - Embed Links
5. Never publish the token or send it in screenshots/logs.

Leave **Interactions Endpoint URL** empty in the Developer Portal. Slash-command interactions are received through the bot's existing Gateway connection, so no public HTTP server is required.

## EXILED configuration

Start the server once with version 0.10.0 to let EXILED add the new fields to `config.yml`, then stop it and fill in the IDs:

```yaml
smoky_plugin_v2:
  is_enabled: true
  debug: false
  restart_empty_round: true
  late_join_spawn:
    is_enabled: true
    max_join_time_seconds: 120
    spawn_after_main_waves: true
    main_wave_join_time_seconds: 60
    spawn_delay_seconds: 1
    class_d_chance: 37.5
    facility_guard_chance: 37.5
    scientist_chance: 25
  pink_candy:
    is_enabled: true
    chance_percent: 20
  warnings:
    is_enabled: true
    notify_player: true
    notification_duration: 30
    notification_message: 'Вы получили предупреждение\nПричина: {reason}'
    max_reason_length: 500
  discord:
    is_enabled: true
    token: "PUT_BOT_TOKEN_HERE"
    prefix: "+"
    guild_id: 111111111111111111
    game_events_channel_id: 222222222222222222
    remote_admin_channel_id: 333333333333333333
    moderation_channel_id: 444444444444444444
    status_update_interval: 30
    status_text: "{players} / {max_players} в игре"
    listen_for_commands: true
    log_ip_addresses: true
    role_groups:
      - discord_role_id: 555555555555555555
        remote_admin_group: owner
      - discord_role_id: 666666666666666666
        remote_admin_group: moderator
    account_linking:
      is_enabled: true
      code_lifetime_minutes: 5
      preserve_native_group: true
```

`late_join_spawn` applies only to a player whose UserId has not appeared in the current round. Players already online when the round starts are remembered immediately. A player who dies, leaves and reconnects therefore remains a spectator. The three chance values are relative weights, so they do not have to add up to exactly 100; negative values are treated as zero. A main MTF wave opens a separate window for `NtfPrivate`, while a main Chaos wave opens it for `ChaosRifleman`; reinforcement mini-waves neither open nor extend that window. The first main wave permanently closes the opening-round Class D, Guard and Scientist window, including when an administrator spawns that wave unusually early.

`pink_candy.chance_percent` is clamped to 0-100. The selected bowl candy is replaced directly, so normal SCP-330 inventory limits, usage counting and hand-severing behavior are preserved.

`role_groups` is checked from top to bottom. The first Discord role owned by the member selects the Remote Admin group. Every referenced group must exist in `config_remoteadmin.txt` and, for plugin permission nodes, in Exiled.Permissions `permissions.yml`.

## Account linking and role synchronization

The user runs this guild slash command in Discord:

```text
/link
```

The bot responds privately with a one-time game-console command:

```text
.link ABCDE-FGHIJ
```

After the verified player enters it, the plugin stores the Steam UserId ↔ Discord User ID link, requests the member's current Discord roles and assigns the first matching `role_groups` entry. The assignment affects only the current connection and is not written to `PermissionsHandler.Members`.

Other account commands:

```text
/link-status   Discord: show the linked Steam UserId
/unlink        Discord: remove the link
.unlink        game console: remove the link
```

Codes are generated with `RandomNumberGenerator`, are bound to the Discord user who requested them, expire after `code_lifetime_minutes`, and are consumed once. Links are one-to-one: a Steam account and a Discord account can each appear only once.

On every player verification the plugin calls Discord's single-member endpoint and uses the current roles; roles are not cached in `account-links.yml`. If Discord is unavailable, the user left the guild, or no mapping exists, no administrative group is granted. With `preserve_native_group: true`, a Steam UserId explicitly present in `config_remoteadmin.txt` is never replaced by Discord synchronization.

## Warning commands

The warning target must be online when `warn` is executed so the plugin can verify the Remote Admin hierarchy. The dedicated server console bypasses this comparison.

```text
warn <player ID, UserId, or nickname> <reason>
warnings <player or UserId>
delwarn <warning ID>
```

Player IDs selected through the Remote Admin interface are accepted both as `2` and in the standard dotted form `2.`.

`warnings` also has the `warns` alias. `delwarn` has the `unwarn` and `rmwarn` aliases. Deleting a warning physically removes it from `warnings.yml`; there is no active/inactive status or retained warning history. `next_id` is never reduced, so deleted IDs are not reused. Every successful change is immediately written to disk and mirrored to the Discord moderation channel when the bot is enabled.

The file is re-read before every warning command and every `.warns` request. If manually edited YAML is invalid, the plugin reports an error and refuses to overwrite it.

Players can open the in-game console and use:

```text
.warns
```

This displays only their own warning IDs, issue times, and reasons. Moderator identities are never included in this response.

Add this permission node to the matching Remote Admin groups in the Exiled.Permissions configuration:

```text
smokyplugin.warnings
```

For example:

```yaml
moderator:
  inheritance: []
  permissions:
    - smokyplugin.warnings
```

The group name must also exist in `config_remoteadmin`. The `warn` and `delwarn` commands require `smokyplugin.warnings` and must pass the Remote Admin hierarchy check. Numeric hierarchy values are not displayed. The dedicated server console bypasses Exiled.Permissions normally.

Discord Developer Mode can be enabled under **User Settings → Advanced → Developer Mode**. After that, right-click a server or channel and select **Copy ID**.

Each entry under `discord_event_logs` is generated as `true`. Change any unwanted event to `false`, for example:

```yaml
  discord_event_logs:
    waiting_for_players: true
    round_started: true
    round_ended: true
    player_hurt: false
    player_interacted_door: false
    player_picked_up_item: false
```

Damage, door and item events can produce many Discord messages on an active server, even though they are enabled by default as requested. Disable those switches if the channel becomes too noisy.

`log_ip_addresses: false` replaces the address in join lines with `REDACTED`. Game-event messages are sent as plain text; Remote Admin and moderation logs remain embeds.

## Expected startup messages

With a valid token and guild ID:

```text
[Discord] Gateway connection established.
[Discord] Bot is ready.
[Discord] Guild slash commands /link, /unlink and /link-status are registered.
```

If the token is empty, the SCP server and the rest of the plugin continue normally, but Discord is not started.

When the game-server process is offline, the bot is also offline and therefore cannot display DND. DND only made sense in the old two-process architecture, where the separate bot could remain running after the game plugin disconnected.

## Build

Open `SmokyPluginV2.sln` in Visual Studio and select `Release | x64`, or run MSBuild with the SCP:SL and EXILED reference paths configured. The output is:

```text
bin/Release/SmokyPluginV2.dll
```
