# SmokyPluginV2

EXILED 9.14.2 plugin for SCP: Secret Laboratory 14.2.7.

Version 0.23.1 stores player statistics, server statistics, account links, referrals and moderation history in PostgreSQL. Multiple SCP:SL instances share one database configuration and are separated automatically by their game ports.

## Current features

- Persistent warning, kick and ban history and one-to-one Steam/Discord links stored in PostgreSQL.
- Offline warnings are delivered by Discord DM when an account link is available; otherwise they remain pending and are queued as broadcasts when the player next joins the issuing server.
- Compact aggregate player statistics, keyed by server and Steam ID, including the best Chaos device Snake score; no per-round history rows are retained.
- Aggregate server statistics, including round outcomes, round duration, warhead detonations and separate MTF/Chaos main and reinforcement waves.
- Discord `/stats` and `/server-stats` embeds, with global per-player privacy controlled by `/stats-privacy`.
- Configurable recurring server-wide broadcasts.
- `statstoggle` (aliases: `togglestats`, `ts`) temporarily pauses statistics for the current round and requires its own EXILED permission.
- `clearstats <game ID|SteamID64>` (aliases: `resetstats`, `cs`) clears one player's statistics only for the current server and requires `smokyplugin.statistics.clear`.
- Statistics are recorded only between `RoundStarted` and `RoundEnded`; lobby and post-round friendly-fire events are never counted. A pause is always reset by the next `RoundStarted`.
- Remote Admin commands: `warn`, `punishments`/`punishmenthistory`/`ph`, and `delpunishment`/`delpunish`/`dp`.
- Player console command `.warns` to view personal warnings without moderator information.
- Separate EXILED permissions control warning issue, history view and history deletion.
- Discord `/punishments` provides a public, paginated moderation-history embed and rechecks the mapped RA group permission on every button interaction.
- Immediate active-round restart after the last player leaves.
- Friendly fire enabled only during the post-round period and forcibly disabled before the next round.
- First-time late joiners can spawn as Class D, Facility Guard or Scientist during a configurable opening window; reconnecting participants cannot spawn again.
- After a main MTF or Chaos wave, first-time joiners have a separate 60-second window to spawn as an MTF Private or Chaos Rifleman. Reinforcement mini-waves are ignored.
- SCP-330 can replace a candy drawn from its bowl with the pink candy at a configurable chance (20% by default).
- Pre-round tower with colored zones for SCP, Научный Сотрудник, Персонал класса D and Охранник Комплекса.
- Native server lobby countdown, selected class, expected slot count, effective weight and exact live probability in a hint.
- Configurable Remote Admin group tiers increase selection weight only when a requested role is oversubscribed.
- `eventlobby` / `elobby` requires `smokyplugin.eventlobby`, toggles a paused event briefing, temporarily mutes participants with the in-game indicator, and keeps configured RA groups audible.
- Classic Discord game-event logs: plain text, timestamps, emoji and one-second batching like the old DiscordIntegration plugin.
- Player join logs include IP addresses by default.
- An individual switch for every game-event type.
- Compact Remote Admin command lines, with several commands combined into one Discord message when they arrive together.
- Separate moderation embeds for kicks, bans, voice/intercom mutes, unmutes and unbans.
- All Discord logs share one bounded worker queue, an 8-request-per-second REST gate, response-header bucket waits, and global `Retry-After` backoff; consecutive embeds for the same channel can be batched up to ten per request.
- A 429 with Discord's global-block message pauses REST for one hour; an otherwise unclassified 429 without `Retry-After` uses bounded exponential backoff from 60 seconds to 15 minutes.
- Bot presence: online while players are connected and idle while the server is empty.
- Activity text: `{players} / {max_players} в игре`.
- Discord `+` commands are executed through Remote Admin with permissions from the mapped Discord role.
- Guild slash commands `/link`, `/unlink`, and `/link-status` with private ephemeral responses.
- One-time Steam-to-Discord linking codes, valid for five minutes and stored only in memory.
- Existing `account-links.yml` and `warnings.yml` files are imported once and retained unchanged as backups.
- Current Discord roles are fetched when a linked player verifies and mapped to a temporary SL Remote Admin group.
- Native groups explicitly assigned in `config_remoteadmin.txt` are preserved by default.

## Installation

Copy `bin/Release/SmokyPluginV2.dll` to the EXILED plugin directory for the correct server port:

```text
~/.config/EXILED/Plugins/<server-port>
```

For port `25566`, for example:

```text
~/.config/EXILED/Plugins/25566
```

Copy `bin/Release/Npgsql.dll` and these nine companion files to EXILED's shared dependency directory:

```text
~/.config/EXILED/Plugins/dependencies/
```

The required files are `Microsoft.Bcl.AsyncInterfaces.dll`, `System.Buffers.dll`,
`System.Memory.dll`, `System.Numerics.Vectors.dll`,
`System.Runtime.CompilerServices.Unsafe.dll`, `System.Text.Encodings.Web.dll`,
`System.Text.Json.dll`, `System.Threading.Tasks.Extensions.dll` and
`System.ValueTuple.dll`. They are shared by all game-server instances and are
not embedded in `SmokyPluginV2.dll`.

Create a PostgreSQL 18 database and user before starting the plugin. The user needs permission to create and alter tables and to read/write rows in that database. The plugin creates its schema automatically; it does not create the database itself. For an existing MariaDB installation, follow [the dump migration guide](migration/README.md) before starting version 0.21.2.

## Discord application setup

1. Create an application and bot at <https://discord.com/developers/applications>.
2. On the **Bot** page, enable **Server Members Intent** so returning guild members can have their linked privilege roles restored. Enable **Message Content Intent** as well when `listen_for_commands` is enabled.
3. Invite the bot to your Discord server with the `bot` scope. The `applications.commands` scope is included automatically with bot installations.
4. Give it these permissions in all configured log channels:
   - View Channel
   - Send Messages
   - Embed Links
5. Give the bot **Manage Roles** at guild level and place its highest role above every role configured for linking or privilege synchronization.
6. Never publish the token or send it in screenshots/logs.

Leave **Interactions Endpoint URL** empty in the Developer Portal. Slash-command interactions are received through the bot's existing Gateway connection, so no public HTTP server is required.

## EXILED configuration

Start one server once with version 0.21.2. The plugin creates the shared database file at:

```text
~/.config/EXILED/Configs/Plugins/smoky_plugin_v2/database.yml
```

Stop the server and fill in the shared PostgreSQL credentials:

```yaml
host: "127.0.0.1"
port: 5432
name: "smoky_plugin_v2"
username: "smoky_plugin_v2"
password: "CHANGE_ME"
use_tls: false
connection_timeout_seconds: 5
maximum_pool_size: 10
```

Every plugin instance reads this same file. Per-server settings remain in `Configs/Plugins/smoky_plugin_v2/<game-port>.yml`; for example, port `7777` uses `7777.yml`:

```yaml
smoky_plugin_v2:
  is_enabled: true
  debug: false
  restart_empty_round: true
  database:
    is_enabled: true
    server_name: "Основной сервер"
    import_legacy_yaml: true
  statistics:
    is_enabled: true
  earned_privileges:
    required_hours: 100
    group_name: pearl
    referrals:
      is_enabled: true
      code_entry_max_minutes: 15
      qualification_minutes: 120
      required_referrals: 5
      pending_referral_weight: 1.25
  end_round_friendly_fire:
    is_enabled: true
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
  general_broadcast:
    is_enabled: false
    interval_seconds: 300
    duration_seconds: 10
    text: "Добро пожаловать на сервер!"
  role_preferences:
    is_enabled: true
    tower:
      is_enabled: true
      use_dynamic_zone_positions: true
      dynamic_zone_wall_gap: 0.18
      dynamic_zone_center_gap: 0.5
      dynamic_zone_size_scale: 0.85
      center: { x: 53.6, y: 1018.4, z: -44.6 }
      scp_zone: { x: 50.9, y: 1018.15, z: -40.7 }
      scientist_zone: { x: 56.3, y: 1018.15, z: -40.7 }
      class_d_zone: { x: 50.9, y: 1018.15, z: -48.5 }
      facility_guard_zone: { x: 56.3, y: 1018.15, z: -48.5 }
      zone_radius: 1.4
      lobby_timer_countdown: 'Раунд начнется через {time} секунд'
      lobby_timer_round_paused: 'Запуск раунда приостановлен'
      lobby_timer_round_starting: 'Раунд начинается!'
      lobby_timer_players_connected: '{players} игроков подключилось'
      random_role_name: 'Случайно'
      scp_role_name: 'SCP'
      scientist_role_name: 'Научный Сотрудник'
      class_d_role_name: 'Персонал класса D'
      facility_guard_role_name: 'Охранник Комплекса'
      selected_class_text: 'Выбранный класс: <color={color}><b>{role}</b></color>'
      probability_text: 'Вероятность: <b>{probability}</b>'
      competition_text: 'Желающих: {requested} · мест: {slots} · вес: {weight}'
      random_instruction_text: 'Войдите в одну из четырёх цветных зон'
      event_briefing:
        is_enabled: true
        announcement_text: 'В этом раунде проводится ивент'
        mute_exempt_groups: [owner, admin]
    default_weight: 1
    priority_tiers:
      - name: sponsor
        weight: 2.5
        groups:
          - sponsor
      - name: premium
        weight: 2
        groups:
          - premium
      - name: vip
        weight: 1.5
        groups:
          - vip
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
      linked_discord_role_id: 777777777777777777
      preserve_native_group: true
```

`late_join_spawn` applies only to a player whose UserId has not appeared in the current round. Players already online when the round starts are remembered immediately. A player who dies, leaves and reconnects therefore remains a spectator. The three chance values are relative weights, so they do not have to add up to exactly 100; negative values are treated as zero. A main MTF wave opens a separate window for `NtfPrivate`, while a main Chaos wave opens it for `ChaosRifleman`; reinforcement mini-waves neither open nor extend that window. The first main wave permanently closes the opening-round Class D, Guard and Scientist window, including when an administrator spawns that wave unusually early.

`pink_candy.chance_percent` is clamped to 0-100. The selected bowl candy is replaced directly, so normal SCP-330 inventory limits, usage counting and hand-severing behavior are preserved.

During `WaitingForPlayers`, every connected player is assigned Tutorial once when they first enter the current tower lobby, and the game places them at its native Tutorial spawn; the lobby update loop never forces their role back afterward. The plugin does not override player coordinates. Remaining in a colored zone for `selection_hold_seconds` changes the preference; moving away does not erase it, and entering another zone replaces it. A selection is valid only for the upcoming round and is cleared when the next lobby begins. The hint reads the game's own `RoundStart.NetworkTimer`, so the displayed duration follows the server's native lobby configuration and lobby pause state.

The displayed probability is calculated exactly for the same sequential weighted draw without replacement used at round start. Candidates with the same effective weight are grouped, and a dynamic program evaluates every reachable winner-count state. The value is deterministic and recalculated whenever a player joins, leaves, changes group, or changes selection.

During the tower lobby, a command sender with the EXILED permission `smokyplugin.eventlobby` may run `eventlobby` (aliases: `elobby`, `eventbriefing`) in Remote Admin. The first use locks the native lobby countdown, replaces the selected-class line with `announcement_text`, hides the probability and competition lines, and sets the non-persistent `Player.IsMuted` flag for tower participants outside the Remote Admin groups listed in `mute_exempt_groups`. Players who join while the briefing is active receive the same temporary mute. The second use or round start unlocks the lobby and removes only mutes owned by this feature; a player present in the server's `mutes.txt` is never unmuted. The persistent `Player.Mute()` and `Player.UnMute()` APIs are not used.

At round start a narrowly scoped Harmony postfix allows only registered tower participants who are still Tutorial to pass `RoleAssigner.CheckPlayer`, and only while `RoleAssigner.OnRoundStarted` is executing. The existing atomic SCP and human spawner hooks then replace Tutorial directly with the final role. No intermediate Spectator role is assigned, which avoids a second post-start role swap and duplicate starting inventories.

`priority_tiers` matches every currently resolved group case-insensitively: the live RA group, a native `config_remoteadmin.txt` group, database privileges, and all RA groups mapped from effective Discord roles. The highest matching tier weight wins without changing the player's assigned RA group. `weight` is relative: with one contested slot, weight `2` is twice as likely as one ordinary weight-`1` player, not a guaranteed win. When several slots exist, winners are drawn one at a time without replacement and weights are recalculated after every winner. The configured weight applies in every contested allocation.

During the pre-round lobby, a moderator with the EXILED permission `smokyplugin.roleweight` may temporarily replace one online player's effective weight:

```text
roleweight <internal player ID> <weight>
```

Aliases: `setweight`, `rw`. The standard dotted Remote Admin selector form (for example, `12.`) is accepted. The weight must be a finite number greater than zero. The override immediately updates the tower hint and is used by the same weighted draw as group priorities. It is removed when the player leaves, the lobby is restarted, the next lobby begins, or the plugin is unloaded; it never changes `priority_tiers` or the player's Remote Admin group.

When `reload configs` recreates the tower during an active lobby, the original native Tutorial spawn is carried over to the new tower instance. Dynamic zone measurement therefore remains anchored to the real spawn instead of using the current position of an already-staged Tutorial player.

Add the separate permission node to the desired group in Exiled.Permissions `permissions.yml`:

```yaml
moderator:
  permissions:
    - smokyplugin.roleweight
```

The tower chooses the general SCP category. During vanilla role generation, the plugin keeps the generated role counts and exact SCP lineup but selects their players before the first role assignment.

`role_groups` is checked from top to bottom. The first Discord role owned by the member selects the Remote Admin group. Every referenced group must exist in `config_remoteadmin.txt` and, for plugin permission nodes, in Exiled.Permissions `permissions.yml`.

After changing `role_groups`, run `reload configs`. The live mapping list is replaced and every linked online player is refreshed; restarting the Discord client is not required. Database connection settings, the bot token, guild ID, or whether the integrated bot is enabled require a full server restart.

## Account linking and role synchronization

The user runs this guild slash command in Discord:

```text
/link
```

The bot responds privately with a one-time game-console command:

```text
.link ABCDE-FGHIJ
```

After the verified player enters it, the plugin stores the Steam ID ↔ Discord User ID link in PostgreSQL and starts the common privilege synchronization. `linked_discord_role_id`, when non-zero, is granted while the link exists. It does not need a `role_groups` entry; if one is added, the same role can also select an RA group.

Other account commands:

```text
/link-status   Discord: show the linked Steam UserId
/unlink        Discord: remove the link
.unlink        game console: remove the link
/referral      Discord: show the permanent referral code and progress
.ref CODE      game console: accept a referral
.janitorcard   game console: receive one janitor keycard per round while the referral is pending (alias: .jc)
```

Codes are generated with `RandomNumberGenerator`, are bound to the Discord user who requested them, expire after `code_lifetime_minutes`, and are consumed once. Links are one-to-one: a Steam account and a Discord account can each appear only once.

On every player verification the plugin resolves the Steam/Discord identity first, then fetches the Discord member and resolves PostgreSQL privilege sources in parallel. The member is fetched at most once and the resulting role snapshot is passed into reconciliation. Active Steam- and Discord-bound privileges are combined, missing roles are queued for assignment, and the highest matching `role_groups` entry is applied in the game without waiting for the role operations to finish. `earned_privileges.group_name` is active after either `earned_privileges.required_hours` or the configured number of qualified referrals; no computed privilege is stored in another table. After `reload remoteadmin`, the same live synchronization is repeated for every linked online player. With `preserve_native_group: true`, a Steam UserId explicitly present in `config_remoteadmin.txt` keeps the game's native group, while managed Discord roles are still reconciled.

Discord reconciliation returns a per-role report distinguishing successful changes, already satisfied state, roles preserved by another active privilege, failures, and cancellation. Privilege sources can attach their persistent source IDs to pending revocations; only a successfully settled removal is passed to the PostgreSQL finalization hook. The current playtime/referral privilege never requests removal, so a manually assigned matching Discord role remains untouched during normal synchronization.

When a user joins the configured Discord guild, `GUILD_MEMBER_ADD` starts a Discord-only privilege synchronization. The complete role list from the Gateway member event is reused instead of issuing another REST lookup. It restores the link role and all active Steam- and Discord-bound privilege roles without touching the player's current in-game group. Members waiting for Discord Membership Screening are synchronized after their `pending` state clears.

A referral code is permanent and is generated lazily by `/referral` for a linked account. Each player can accept one code with `.ref CODE` before `code_entry_max_minutes`; the number of players using an inviter's code is unlimited. Qualification is derived from aggregate playtime in `player_statistics`; no qualified flag or duplicate playtime is stored. Pending invitees receive `pending_referral_weight`, while the inviter receives the earned group after `required_referrals` invitees each reach `qualification_minutes`.

On unlink, the old Discord ID is retained for the cleanup pass. The dedicated link role and every currently active Steam-bound privilege role are removed from that Discord account; Discord-bound and unrelated roles remain untouched. The same Steam privilege snapshot is reused to recalculate an online game player after the Discord account is no longer linked.

## Moderation history commands

`warn` accepts an online selector or an exact offline SteamID64. Native RA hierarchy is checked in both cases. The dedicated server console bypasses this comparison.

```text
warn <player ID, UserId, nickname, or SteamID64> <reason>
punishments <player or SteamID64>
delpunishment <punishment ID>
```

Player IDs selected through the Remote Admin interface are accepted both as `2` and in the standard dotted form `2.`.

`punishments` has the `punishmenthistory` and `ph` aliases. `delpunishment` has the `delpunish` and `dp` aliases. IDs are global sequential PostgreSQL identity values. The delete command physically removes only the selected history row. A successful native RA unban by SteamID64 removes matching active ban rows automatically; expired bans remain in history.

Players can open the in-game console and use:

```text
.warns
```

This displays only their own warning IDs, issue times, and reasons. Moderator identities are never included in this response.

Add the required permission nodes to matching groups in the Exiled.Permissions configuration:

```text
smokyplugin.moderation.warning.issue
smokyplugin.moderation.history.view
smokyplugin.moderation.history.delete
```

For example:

```yaml
moderator:
  inheritance: []
  permissions:
    - smokyplugin.moderation.warning.issue
    - smokyplugin.moderation.history.view
    - smokyplugin.moderation.history.delete
    - smokyplugin.statistics.toggle
    - smokyplugin.statistics.clear
```

The group name must also exist in `config_remoteadmin`. Exiled.Permissions inheritance and wildcard permissions are honored. Numeric hierarchy values are not displayed. The dedicated server console bypasses Exiled.Permissions normally.

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

`log_ip_addresses: false` replaces the address in join lines with `REDACTED`. Game-event and Remote Admin command messages are sent as batched plain text; moderation logs remain embeds.

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
