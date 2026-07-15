# Minecraft Portable

Минималистичный portable-лаунчер для одиночной игры и `Open to LAN`. Он запускает локальные модпаки, хранит миры отдельно, умеет передавать мир между игроками, находить участников виртуальной сети и предоставляет встроенный голосовой канал.

## Скачать

[Скачать последнюю версию Minecraft.exe](https://github.com/MarkZamore/Minecraft/releases/latest/download/Minecraft.exe)

Для первого запуска достаточно поместить `Minecraft.exe` рядом с папкой `Minecraft\Packs`, содержащей подготовленную сборку. Игровые runtime-файлы выбранной версии загружаются после нажатия `Играть`; сама сборка, её моды и client jar приложением не скачиваются.

## Структура

```text
Minecraft.exe
Program\                         исходники приложения
Minecraft\
  Packs\<Сборка>\               распространяемый read-only модпак
  Launcher\Runtimes\<Сборка>\  загруженный runtime выбранной сборки
  Personal\Instances\<Сборка>\ личные настройки, логи и writable game directory
  Worlds\                        одиночные миры
```

`Minecraft.exe` и GitHub-репозиторий не содержат Minecraft client/server jar, модпаки, Java или заранее подготовленный игровой runtime.

## Подготовка Сборки

Создай `Minecraft\Packs\<Название>` и положи туда файлы модпака. Обычно это:

```text
mods\
config\
defaultconfigs\
kubejs\
scripts\
resourcepacks\
data\
patchouli_books\
options.txt
minecraft-<версия>-client.jar
portable-pack.json
```

Папка `mods` необязательна, поэтому так же можно создать чистую Vanilla-сборку. Не добавляй аккаунты, токены, `servers.dat`, миры, логи, crash reports, launcher/runtime-файлы или server jar.

Каждой сборке нужен `portable-pack.json`:

```json
{
  "schemaVersion": 1,
  "minecraftVersion": "1.21.1",
  "loader": {
    "type": "neoforge",
    "version": "21.1.224"
  },
  "clientJar": "minecraft-1.21.1-client.jar"
}
```

Поддерживаемые `loader.type`:

- `vanilla` — поле `loader.version` не указывается;
- `forge`;
- `neoforge`;
- `fabric`;
- `quilt`.

Для mod loader всегда указывай точную версию, совместимую с `minecraftVersion`. `clientJar` должен быть только именем jar-файла прямо в корне сборки: абсолютные пути и `..` запрещены.

Client jar приложение не скачивает и не заменяет. Пользователь предоставляет свою законно полученную копию; перед запуском она сверяется с размером и SHA-1 из официальных metadata Mojang и проверяется на наличие client entry point.

## Первый Запуск

После нажатия `Играть` приложение автоматически:

1. Проверяет manifest и локальный client jar.
2. Скачивает официальные metadata, подходящую Java, libraries, natives, asset index и assets.
3. Устанавливает точную версию Forge, NeoForge, Fabric или Quilt.
4. Сохраняет проверенный runtime в `Minecraft\Launcher\Runtimes\<Сборка>`.
5. Запускает игру из writable instance `Minecraft\Personal\Instances\<Сборка>`.

Моды и конфиги через интернет не скачиваются. Исходная папка в `Packs` не изменяется. После успешной подготовки повторный запуск работает без интернета; повреждённый или изменённый runtime ремонтируется при наличии сети.

Поддержка будущей версии Minecraft возможна без обновления приложения, пока официальные metadata и API выбранного загрузчика сохраняют совместимый формат. Если формат изменится, приложение покажет ошибку подготовки и потребуется обновить `Minecraft.exe`.

## Сборка Приложения

Для самостоятельной сборки нужны .NET SDK 10 и Java Development Kit 21 для Windows x64. Java используется для компиляции встроенного identity-adapter.

Исходники identity-adapter разделены на общий код и реализацию конкретной версии:

```text
Program\IdentityAdapters\
  Common\
    Build-IdentityAdapter.ps1
    MANIFEST.MF
    PortableIdentityAgent.java
    PortableIdentityReflection.java
  Minecraft-1.21.1-NeoForge\
    PortableIdentityHooks.java
    PortableIdentityTransformer.java
```

Новый адаптер создаётся отдельной папкой внутри `IdentityAdapters`. Он предоставляет свои классы `PortableIdentityHooks` и `PortableIdentityTransformer` в package `minecraft.portable.identity`; сборщик компилирует их вместе с `Common`, поэтому общий agent и reflection-код не копируются.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Program\Publish.ps1
```

Release собирается как self-contained single-file и записывается строго в корень проекта:

```text
Minecraft.exe
```

## Автообновление

Push в `main` запускает GitHub Actions. Workflow собирает приложение, создаёт `update.json` и публикует в release `latest` полный `Minecraft.exe` и прямые delta-патчи от двух предыдущих релизов. Более старые версии незаметно используют полный файл.

При каждом запуске приложение проверяет уже скачанное обновление, затем публичный GitHub Release. Новая версия автоматически скачивается в фоне и проверяется по размеру и SHA-256. В том же сеансе установка выполняется кнопкой `Обновить`. Если пользователь закрыл приложение, не нажав кнопку, при следующем запуске готовое обновление автоматически устанавливается с перезапуском. Перед установкой приложение кратко проверяет `latest` и при необходимости заменяет кэш более новой версией; без интернета применяется уже проверенный файл. Если обновления ещё нет и GitHub недоступен, приложение не показывает ошибку и продолжает работу в состоянии `Вы на последней версии`.

## Репозиторий

В Git хранятся только исходники `Program`, workflow и корневой `.gitignore`. Папка `Minecraft`, готовый `Minecraft.exe`, modpacks, client/server jar, runtime, миры, логи и персональные файлы игнорируются.

Лицензия исходников приложения: MIT.
