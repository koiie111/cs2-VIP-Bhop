# [VIP] Bhop: native cvars

Модуль для [VIP Core](https://github.com/partiusfabaa/cs2-VIPCore) (CounterStrikeSharp): автобхоп **только для VIP-игроков** на встроенных переменных CS2 `sv_autobunnyhopping` / `sv_enablebunnyhopping`. Прыжки идут без прилипаний и дёрганий, у остальных игроков движение ванильное.

A [VIP Core](https://github.com/partiusfabaa/cs2-VIPCore) module for CounterStrikeSharp: **VIP-only** auto bunnyhop driven by CS2's own `sv_autobunnyhopping` / `sv_enablebunnyhopping`, with no sticking or jitter. Everyone else keeps vanilla movement. English section is [below](#english).

> **Статус: beta.** Собирается под CounterStrikeSharp 1.0.376 / CS2 1.41.8.x (обновление 23.09.2026). На боевых серверах пока проверен мало, поэтому перед запуском посмотрите раздел [Диагностика](#диагностика).

---

## Зачем это нужно

Оригинальный модуль `VIP_Bhop` делает бхоп так: подкидывает игрока (`AbsVelocity.Z = 300`) на сервере и каждый тик отправляет клиенту `sv_autobunnyhopping true`. Из-за этого возникают две проблемы:

- **Рассинхрон.** Клиент прыгает по встроенному автобхопу, а сервер считает движение с `false`. Отсюда прилипания к земле и дёрганья.
- **Утечка на всех.** Если `ReplicateConVar` ведёт себя неправильно (например, после обновления CS2, пока CSS не обновлён), значение `true` может уйти всем клиентам.

## Как работает

Тот же приём, что у стиля autobhop в [cs2kz-metamod](https://github.com/KZGlobalTeam/cs2kz-metamod):

1. С `sv_autobunnyhopping` и `sv_enablebunnyhopping` снимается флаг `FCVAR_REPLICATED`, и сервер больше не рассылает их всем.
2. Каждому клиенту значение отправляется отдельно (`CNETMsg_SetConVar` одному получателю) и только при изменении: VIP с активным бхопом получает `true`, остальные — реальное значение сервера.
3. Функция `CCSPlayer_MovementServices::ProcessMovement` перехватывается. На время движения VIP общее значение в памяти ставится в `true` и сразу возвращается обратно. Колбэки изменения не вызываются, рассылки нет.
4. Перед движением любого не-VIP принудительно ставится реальное значение. Ещё значение возвращается после фазы обработки сущностей и каждый тик. Поэтому подмена не может «утечь» на других игроков, даже там, где post-хук не срабатывает (CSS 1.0.375+).

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

Сигнатура хранится в отдельном файле [`gamedata/vip_bhop.json`](gamedata/vip_bhop.json):

```json
{
  "VIP_Bhop_CCSPlayer_MovementServices_ProcessMovement": {
    "signatures": {
      "library": "server",
      "windows": "40 57 41 57 48 81 EC ? ? ? ? 48 83 79",
      "linux": "55 48 89 E5 41 57 41 56 41 55 49 89 F5 41 54 53 48 89 FB 48 83 EC ? 48 8B 7F"
    }
  }
}
```

- CSS при старте загружает все `*.json` из папки gamedata. Автообновление CSS перезаписывает только свой `gamedata.json`, так что этот файл не затрётся.
- Ключ уникальный, чтобы устаревшая запись с тем же именем от другого плагина его не перекрыла.
- После обновлений CS2 актуальные сигнатуры можно взять в [ianlucas/cs2-signatures](https://github.com/ianlucas/cs2-signatures) (CS2Fixes / SwiftlyS2 → `ProcessMovement`, колонка IDA-Style). После правки файла перезапустите сервер.
- Если сигнатура не найдена, плагин пишет ошибку в лог и **ничего не меняет**: переменные остаются как на ванильном сервере.

## Диагностика

Команда `css_vipbhop_status` доступна только из консоли сервера или rcon:

```
[VIP Bhop] hook: OK
[VIP Bhop] real values: sv_autobunnyhopping=False sv_enablebunnyhopping=False, overridden now: False
[VIP Bhop] flags: ...
[VIP Bhop] ProcessMovement pre=123456 post=123456 vip=4321 staleRestores=0
[VIP Bhop] #0 Player: enabled=True active=True sent=True/True inMovementSet=True
```

- `real values` должны совпадать с вашим `server.cfg`. Если там `True`, переменную включает конфиг или другой плагин, а не этот модуль. Проверьте так: `css_plugins unload VIP_Bhop`, затем `sv_autobunnyhopping`.
- `post=0` на CSS 1.0.375+ (KHook) — это нормально: post-хук `ProcessMovement` там не вызывается. Модуль от него не зависит. Реальное значение возвращается перед движением каждого не-VIP (`staleRestores`) и сразу после фазы обработки сущностей в каждом кадре.
- `vip` должен расти только пока VIP двигается, а у обычных игроков должно быть `enabled=False sent=False/False inMovementSet=False`.

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
- `CCSPlayer_MovementServices::ProcessMovement` is hooked. The global value is written straight into memory as `true` only while a VIP's movement runs, and it is restored right afterwards.
- Non-VIPs are always forced back to the real value, so client prediction and server simulation match.

**Install:**
1. Remove the original `VIP_Bhop`.
2. Extract the release zip into `game/csgo/`.
3. Restart the server; gamedata is only loaded at startup.

**Group config:** `"Bhop": { "Timer": 5, "MaxSpeed": 0 }`. `Timer` is the activation delay after freezetime; `MaxSpeed` is a horizontal speed cap, `0` means no cap.

**Signatures** live in `addons/counterstrikesharp/gamedata/vip_bhop.json`. Get fresh ones from [ianlucas/cs2-signatures](https://github.com/ianlucas/cs2-signatures). If the signature is missing or outdated, the module logs an error and does nothing.

**Diagnostics:** run `css_vipbhop_status` from the server console.

**Requirements:** CounterStrikeSharp >= 1.0.375 (.NET 10), VIP Core.

## Credits

- [thesamefabius / partiusfabaa](https://github.com/partiusfabaa/cs2-VIPCore): VIP Core and the original `VIP_Bhop` module (MIT)
- [KZGlobalTeam/cs2kz-metamod](https://github.com/KZGlobalTeam/cs2kz-metamod): per-player cvar technique
- [ianlucas/cs2-signatures](https://github.com/ianlucas/cs2-signatures): signature tracker

## License

[MIT](LICENSE)
