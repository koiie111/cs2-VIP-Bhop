# [VIP] Bhop: native cvars

Модуль для [VIP Core](https://github.com/partiusfabaa/cs2-VIPCore) (CounterStrikeSharp): автобхоп **только для VIP-игроков** на встроенных переменных CS2 `sv_autobunnyhopping` / `sv_enablebunnyhopping`. Прыжки идут без прилипаний и дёрганий, у остальных игроков движение ванильное.

A [VIP Core](https://github.com/partiusfabaa/cs2-VIPCore) module for CounterStrikeSharp: **VIP-only** auto bunnyhop driven by CS2's own `sv_autobunnyhopping` / `sv_enablebunnyhopping`, with no sticking or jitter. Everyone else keeps vanilla movement. English section is [below](#english).

> Проверено на сервере с CounterStrikeSharp 1.0.376 / CS2 1.41.8.x (обновление 23.09.2026), версии v2.6.0–v2.6.1: у VIP бхоп без прилипаний и телепортов, у обычных игроков движение ванильное (проверено через `css_vipbhop_trace`).

---

## Зачем это нужно

Оригинальный модуль `VIP_Bhop` делает бхоп так: подкидывает игрока (`AbsVelocity.Z = 300`) на сервере и каждый тик отправляет клиенту `sv_autobunnyhopping true`. Из-за этого возникают две проблемы:

- **Рассинхрон.** Клиент прыгает по встроенному автобхопу, а сервер считает движение с `false`. Отсюда прилипания к земле и дёрганья.
- **Утечка на всех.** Если `ReplicateConVar` ведёт себя неправильно (например, после обновления CS2, пока CSS не обновлён), значение `true` может уйти всем клиентам.

## Как работает

Тот же приём, что у стиля autobhop в [cs2kz-metamod](https://github.com/KZGlobalTeam/cs2kz-metamod):

1. С `sv_autobunnyhopping` и `sv_enablebunnyhopping` снимается флаг `FCVAR_REPLICATED`, и сервер больше не рассылает их всем.
2. Каждому клиенту значение отправляется отдельно (`CNETMsg_SetConVar` одному получателю) и только при изменении: VIP с активным бхопом получает `true`, остальные — реальное значение сервера.
3. Перехватываются точки входа в обработку каждого игрока: `CCSPlayerController::ProcessUsercmds` → внутри неё `CBasePlayerController::OnSimulateUserCommands` → `CCSPlayer_MovementServices::SetupMove` → `CCSPlayer_MovementServices::ProcessMovement`, а также `CCSPlayerPawnBase::PostThink` пешки (сигнатуру поддерживает сам CSS). На входе в каждую для **каждого** игрока в память пишется его значение: VIP получает `true`, остальные — реальное значение сервера. Колбэки изменения не вызываются, рассылки нет.
4. Хуки только меняют значение переменной: плагин никогда сам не вызывает и не пропускает функции игры. На CSS 1.0.375+ (KHook) такой приём приводил к повторной обработке команд VIP (сверхскорость, телепорты, падение сервера). Post-хуки тоже не используются, там они не срабатывают.
5. Значение VIP сбрасывается на входе следующего игрока, после фазы обработки сущностей и каждый тик.

В итоге клиент и сервер считают движение VIP одинаково по встроенному автобхопу CS2.

## Требования

- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) **>= 1.0.375** (.NET 10)
- [VIP Core](https://github.com/partiusfabaa/cs2-VIPCore)

## Установка

1. Скачайте `VIP_Bhop.zip` из [Releases](../../releases) или соберите сами (см. ниже).
2. **Удалите оригинальный** `addons/counterstrikesharp/plugins/VIP_Bhop/`. Фича называется так же (`Bhop`), вместе они работать не могут.
3. Распакуйте архив в `game/csgo/`:
   ```
   addons/counterstrikesharp/plugins/VIP_Bhop/VIP_Bhop.dll
   addons/counterstrikesharp/gamedata/vip_bhop.json
   ```
4. **Перезапустите сервер.** CSS читает gamedata только при старте.

## Настройка групп VIP Core

Формат тот же, что у оригинального модуля (`addons/counterstrikesharp/configs/plugins/VIPCore/vip.json`):

```json
"Bhop": {
  "Timer": 5,
  "MaxSpeed": 0
}
```

| Параметр | Описание |
|---|---|
| `Timer` | Через сколько секунд после окончания freezetime включается бхоп. В разминке бхоп включается сразу. |
| `MaxSpeed` | Ограничение горизонтальной скорости при прыжке. `0` = без ограничения. Ограничение делается на сервере, поэтому при срабатывании возможен небольшой рывок. Для максимально плавного бхопа ставьте `0`. |

Тексты сообщений (`bhop.TimeToActivation`, `bhop.Activated`) берутся из языковых файлов VIP Core.

## Gamedata / сигнатуры

Сигнатуры хранятся в отдельном файле [`gamedata/vip_bhop.json`](gamedata/vip_bhop.json):

| Ключ | Функция в [cs2-signatures](https://github.com/ianlucas/cs2-signatures) |
|---|---|
| `VIP_Bhop_CBasePlayerController_OnSimulateUserCommands` | SwiftlyS2 → `CBasePlayerController::OnSimulateUserCommands` (у cs2kz называется `PhysicsSimulate`) |
| `VIP_Bhop_CCSPlayerController_ProcessUsercmds` | CS2Fixes / cs2kz-metamod → `ProcessUsercmds` |
| `VIP_Bhop_CCSPlayer_MovementServices_SetupMove` | SwiftlyS2 → `CCSPlayer_MovementServices::SetupMove` (cs2kz → `SetupMove`) |
| `VIP_Bhop_CCSPlayer_MovementServices_ProcessMovement` | CS2Fixes / SwiftlyS2 → `ProcessMovement` |

- CSS при старте загружает все `*.json` из папки gamedata. Автообновление CSS перезаписывает только свой `gamedata.json`, так что этот файл не затрётся.
- Ключ уникальный, чтобы устаревшая запись с тем же именем от другого плагина его не перекрыла.
- После обновлений CS2 берите значения из колонки IDA-Style и перезапускайте сервер.
- `OnSimulateUserCommands`, `SetupMove` и `ProcessMovement` обязательны. Если хотя бы одна из них не найдена, плагин пишет ошибку в лог и **ничего не меняет**: переменные остаются как на ванильном сервере.
- `ProcessUsercmds` необязательна, но без неё `true` от VIP может достаться игроку, который обрабатывается сразу после него. В логе будет предупреждение.
- `PostThink` берётся из gamedata самого CSS, отдельный ключ не нужен.
- При загрузке в лог пишется строка `[VIP Bhop] vX loaded: ... ProcessUsercmds OK/NOT FOUND`.

## Диагностика

Команда `css_vipbhop_status` работает из консоли сервера, через rcon, а также из консоли клиента для админов с `@css/root`. В консоли клиента CS2 сначала пишет `Unknown command`, но команда всё равно уходит на сервер, и ответ появляется ниже.

```
[VIP Bhop] hook: OK
[VIP Bhop] real values: sv_autobunnyhopping=False sv_enablebunnyhopping=False, overridden now: False
[VIP Bhop] flags: ...
[VIP Bhop] v2.4.0, ProcessUsercmds hook: OK
[VIP Bhop] usercmds=40000 simulate=40100 processMovement=40300 vip=60000 restores=12000
[VIP Bhop] #0 Player: enabled=True active=True sent=True/True inMovementSet=True inControllerSet=True simulate/tick=1.00
```

- `real values` должны совпадать с вашим `server.cfg`. Если там `True`, переменную включает конфиг или другой плагин, а не этот модуль. Проверьте так: `css_plugins unload VIP_Bhop`, затем `sv_autobunnyhopping`.
- `usercmds`, `simulate` и `processMovement` должны быть **примерно равны**. Если `processMovement` заметно больше `simulate`, команды кого-то обрабатываются повторно: создайте issue и приложите вывод команды.
- `restores` — сколько раз на входе обычного игрока значение VIP было сброшено до реального. Рост — это нормально.
- `simulate/tick` у каждого игрока должен быть около `1.00`. Если у кого-то заметно меньше, часть его команд обрабатывается вне игрового потока, где CSS не вызывает хуки плагинов. Тогда подмена переменной для этого игрока ненадёжна. Создайте issue и приложите вывод команды.
- У обычных игроков должно быть `enabled=False sent=False/False inMovementSet=False inControllerSet=False`.
- Если обычный игрок всё равно распрыгивается с зажатым пробелом, выполните `sv_autobunnyhopping` **в консоли его клиента**. Если там `true`, значение утекает к клиенту по сети (например, из-за устаревшего CSS). Если `false`, проблема на стороне сервера.

Если обычный игрок распрыгивается при активном VIP, выполните `css_vipbhop_trace`, пока оба прыгают. В консоль сервера выведется порядок вызовов за 3 тика: `U` = ProcessUsercmds, `S` = OnSimulateUserCommands, `P` = SetupMove, `M` = ProcessMovement, `T` = PostThink, затем слот игрока, `*` у VIP и значение `sv_autobunnyhopping` на входе (`+`/`-`). `PRE`/`POST` — начало и конец фазы обработки сущностей. Приложите вывод к issue.

### Нагрузка

Хуки срабатывают примерно 5 раз на игрока за тик (приём команд, SetupMove, ProcessMovement, PostThink, симуляция). Каждый обработчик делает одно чтение параметра, поиск в `HashSet` и чтение/запись значения переменной по закэшированному адресу. Строка `handler time` в `css_vipbhop_status` показывает реальное время обработчиков на вашем сервере. Время перехода CSS из движка в C# в неё не входит, оно обычно составляет единицы микросекунд на вызов. Замер на живом сервере (v2.6.1, 3 игрока): 2,74 мкс на вызов, 159 мс за ~57 секунд — около 0,3 % одного ядра. Нагрузка растёт линейно с числом игроков: при ~20 игроках это около 2 % ядра плюс время перехода CSS из движка в C#.

Найти, где в конфигах задаются эти переменные:

```bash
grep -rniE "autobunnyhopping|enablebunnyhopping" game/csgo/cfg game/csgo/addons game/csgo/gamemodes*.txt
```

## Сборка

```bash
dotnet build VIP_Bhop.csproj -c Release
```

Результат появится в `build/addons/counterstrikesharp/...` в том же виде, что на сервере. `lib/VipCoreApi.dll` взят из [cs2-VIPCore](https://github.com/partiusfabaa/cs2-VIPCore) (MIT) и в сборку не копируется. При пуше тега `v*` GitHub Actions собирает релиз с `VIP_Bhop.zip`.

---

## English

**VIP-only auto bunnyhop** for [VIP Core](https://github.com/partiusfabaa/cs2-VIPCore), built on CS2's native movement cvars. It is a drop-in replacement for the original `VIP_Bhop` module: same feature name (`Bhop`) and the same group settings.

**How it works** (same technique as the cs2kz-metamod autobhop style):
- `FCVAR_REPLICATED` is removed from `sv_autobunnyhopping` and `sv_enablebunnyhopping`, so the server never broadcasts them.
- Each client receives its own value through a single-recipient `CNETMsg_SetConVar`: `true` for an active VIP, the real server value for everyone else.
- `CCSPlayerController::ProcessUsercmds` (commands received) and `CBasePlayerController::OnSimulateUserCommands` (commands simulated: `SetupMove`, `ProcessMovement`, ...) are hooked. `ProcessMovement` is hooked as a second guard.
- At each of these entry points every player gets their own value written straight into memory: `true` for an active VIP, the real value for everyone else. Hooks are pass-through: the plugin never calls or skips game functions (on CSS 1.0.375+/KHook that re-ran VIP commands: speedhack, teleports) and uses no post hooks.

**Install:**
1. Remove the original `VIP_Bhop`.
2. Extract the release zip into `game/csgo/`.
3. Restart the server; gamedata is only loaded at startup.

**Group config:** `"Bhop": { "Timer": 5, "MaxSpeed": 0 }`. `Timer` is the activation delay after freezetime; `MaxSpeed` is a horizontal speed cap, `0` means no cap.

**Signatures** live in `addons/counterstrikesharp/gamedata/vip_bhop.json`. Get fresh ones from [ianlucas/cs2-signatures](https://github.com/ianlucas/cs2-signatures). `OnSimulateUserCommands`, `SetupMove` and `ProcessMovement` are required; if either is missing or outdated, the module logs an error and does nothing. `ProcessUsercmds` is optional: without it a VIP's value may reach the player processed right after them.

**Diagnostics:** run `css_vipbhop_status` from the server console, or from the client console as a `@css/root` admin.

**Requirements:** CounterStrikeSharp >= 1.0.375 (.NET 10), VIP Core.

## Credits

- [thesamefabius / partiusfabaa](https://github.com/partiusfabaa/cs2-VIPCore): VIP Core and the original `VIP_Bhop` module (MIT)
- [KZGlobalTeam/cs2kz-metamod](https://github.com/KZGlobalTeam/cs2kz-metamod): per-player cvar technique
- [ianlucas/cs2-signatures](https://github.com/ianlucas/cs2-signatures): signature tracker

## License

[MIT](LICENSE)
