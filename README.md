# DevScope

Локальный обзор работы сотрудника по данным YouTrack и GitLab. Задачи сотрудника
определяются фильтрами worklog `work author` и `work date`. Поле
разработчика/исполнителя задачи не используется вместо списаний.

## Запуск с реальными MCP

### 1. Проверьте локальные файлы

В каталоге `config` должны находиться:

- `mcp.json` — перенесённый вручную конфиг remote MCP в формате OpenCode;

Файл исключён из Git. DevScope ничего не копирует из OpenCode и не выводит
заголовки или токены в API.

Список сотрудников в real-режиме загружается непосредственно из группы YouTrack
`Fintender-Backend`. Сервисные учётные записи с префиксом `hq-svc-`, а также
заблокированные и отключённые пользователи в список не включаются. Другие группы
можно указать через `DEVSCOPE_DEVELOPER_GROUP`.

Бэкенд запускается непосредственно на Windows и использует корпоративное
хранилище доверенных сертификатов Windows. Отключать TLS validation или добавлять
`--insecure` не требуется.

В `.env.real` укажите ключ проекта:

```text
DEVSCOPE_PROJECT_KEY=FINTDR
DEVSCOPE_DEVELOPER_GROUP=Fintender-Backend
DEVSCOPE_GITLAB_BASE_URL=https://gitlab.example.com
```

`DEVSCOPE_GITLAB_BASE_URL` используется как резервный адрес для формирования
ссылки на MR, если GitLab MCP не вернул `web_url`. Сначала приложение пытается
получить прямую ссылку или адрес GitLab из ответа MCP, и только затем использует
это значение.

PostgreSQL поднимается тем же compose-файлом. При необходимости измените локальные
параметры базы в `.env.real`:

```text
DEVSCOPE_DB_NAME=devscope
DEVSCOPE_DB_USER=devscope
DEVSCOPE_DB_PASSWORD=devscope-local-only
DEVSCOPE_DAILY_REFRESH_HOUR=3
```

### 2. Запуск и очистка MCP-кэша внутри Canvas

Команды выполняйте в терминале Canvas, открытом в корне репозитория DevScope.
Запускать их на пользовательском компьютере не требуется.

Для запуска нужны Docker Desktop, .NET SDK 10 и Node.js. Docker используется
только для PostgreSQL; API и frontend собираются и запускаются на Windows, чтобы
корпоративные MCP работали через доверенное хранилище Windows.

Обычный запуск или перезапуск сохраняет снимки активности в PostgreSQL:

```powershell
cd C:\Work\Repositories\DevScope
.\run-windows.cmd
```

Если менялись правила разбора GitLab/YouTrack, контракты кэша или атрибуция
действий сотрудника, перезапустите приложение с удалением старых снимков:

```powershell
cd C:\Work\Repositories\DevScope
.\run-windows.cmd --clear-cache
```

Режим `--clear-cache` во время старта удаляет только записи `CachedReports`.
PostgreSQL volume, настройки приложения, выбранная LLM-модель и исходные данные
MCP сохраняются. После сообщения `Application started` обновите страницу браузера
и повторно откройте сотрудника и диапазон дат — первый запрос заново получит
данные через реальные MCP и создаст свежий снимок.

Не используйте для очистки кэша `docker compose down -v`: эта команда удаляет
весь PostgreSQL volume вместе с настройками.

Окно `run-windows.cmd` нужно оставить открытым на время работы DevScope. Для
остановки нажмите `Ctrl+C`. В real-режиме mock-контейнер не запускается.

### 3. Проверьте запуск

```powershell
docker compose -f compose.yaml -f compose.windows.yaml --env-file .env.real ps
Invoke-RestMethod http://127.0.0.1:8080/api/health
Invoke-RestMethod http://127.0.0.1:8080/api/configuration/mcp/tools
```

Откройте `http://127.0.0.1:8080`.

### Применить изменения фронта и бэкенда

После `git pull` или редактирования исходников остановите текущее окно через
`Ctrl+C` и снова запустите:

```powershell
.\run-windows.cmd
```

Скрипт повторно соберёт frontend, скопирует его в API и запустит актуальный
бэкенд. PostgreSQL volume и кэш при обычном запуске сохраняются.

Проверить соединение и увидеть реальные методы каждого MCP:

```text
http://127.0.0.1:8080/api/configuration/mcp/tools
```

У каждого сервера должно быть `"connected": true`. Поле `tools` содержит методы,
которые MCP действительно отдал приложению; секретные заголовки в ответ не входят.

Первое открытие сотрудника и периода получает данные через MCP и сохраняет снимок
этого диапазона в PostgreSQL. Для каждого дня выполняется read-only поиск задач по
`work author` и `work date`; так строится дневная карта списаний. Детали задач и
связанные GitLab MR, ветки, коммиты и обсуждения кэшируются вместе со снимком.
Фоновая джоба обновляет только диапазоны, которые уже открывались пользователем.

На экране «Настройки» выбираются:

- единая LLM-модель (по умолчанию `smart`);
- обновление кэша раз в час или раз в день (по умолчанию — раз в день в 03:00);
- немедленное обновление кэша кнопкой «Обновить сейчас».

Настройки хранятся в PostgreSQL и сохраняются после перезапуска. Фоновая джоба
обновляет только MCP-кэш и не вызывает LLM.

## Необходимые методы YouTrack MCP

Для списка сотрудников используются:

```text
youtrack_get_user_group_members(group, offset, limit)
```

Для поиска задач со списаниями используется:

```text
youtrack_search_issues(
  query: "project: FINTDR work author: <login> work date: <from> .. <to>",
  customFieldsToReturn: [],
  offset,
  limit)
```

Фильтры применяются к work items внутри задач и достоверно подтверждают автора и
день списания. MCP не возвращает минуты, тип и текст каждой отдельной записи,
поэтому real-режим не выдаёт накопительные `$Факт ... ЧЧ` за часы выбранного
периода. В интерфейсе показываются задачи и дни подтверждённых списаний.

## Формат `config/mcp.json`

Формат совпадает с объектом `mcp` OpenCode. Минимальный пример:

```json
{
  "mcp": {
    "youtrack": {
      "url": "https://ai-gateway.example/mcp/youtrack",
      "type": "remote",
      "headers": {
        "X-MCP-Token": "REPLACE_LOCALLY",
        "X-Request-By": "personal",
        "X-Youtrack-URL": "youtrack.example",
        "X-MCP-Request-Principal": "user@example"
      }
    },
    "gitlab": {
      "url": "https://ai-gateway.example/mcp/gitlab",
      "type": "remote",
      "headers": {
        "X-MCP-Token": "REPLACE_LOCALLY",
        "X-Request-By": "personal",
        "X-MCP-Request-Principal": "user@example"
      }
    }
  }
}
```

Безопасный полный шаблон находится в `config/mcp.example.json`.

## Mock-сотрудники

`config/teams.json` используется только локальным mock-сценарием. В real-режиме
этот файл не требуется.

## Запуск с mock MCP

```powershell
docker compose --env-file .env.mock --profile mock up --build -d
```

Этот режим поднимает DevScope, PostgreSQL и отдельный Streamable HTTP mock MCP.
Порты PostgreSQL 5432 и mock MCP 8090 доступны только внутри Docker-сети.

## Сетевые порты

Windows-бэкенд и PostgreSQL доступны только через loopback:

```text
127.0.0.1:8080 -> DevScope.Api на Windows
127.0.0.1:5432 -> PostgreSQL в Docker
```

MCP endpoints исходящие. Не меняйте адрес привязки на `0.0.0.0`.

## Остановка

Остановите Windows-бэкенд сочетанием `Ctrl+C` в окне `run-windows.cmd`. Если
PostgreSQL больше не нужен, остановите и его:

```powershell
docker compose -f compose.yaml -f compose.windows.yaml --env-file .env.real stop postgres
```

Для mock-режима:

```powershell
docker compose --env-file .env.mock --profile mock down
```

## Проверка разработки

```powershell
dotnet build src/DevScope.Api/DevScope.Api.csproj
dotnet build src/DevScope.MockMcp/DevScope.MockMcp.csproj
cd src/DevScope.Web
npm run build
```
