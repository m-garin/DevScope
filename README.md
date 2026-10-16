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

Список сотрудников в real-режиме загружается из `teamUserGroup` проекта YouTrack.
Сервисные учётные записи с префиксом `hq-svc-` в список не включаются.

В `.env.real` укажите ключ проекта:

```text
DEVSCOPE_PROJECT_KEY=FINTDR
```

PostgreSQL поднимается тем же compose-файлом. При необходимости измените локальные
параметры базы в `.env.real`:

```text
DEVSCOPE_DB_NAME=devscope
DEVSCOPE_DB_USER=devscope
DEVSCOPE_DB_PASSWORD=devscope-local-only
DEVSCOPE_DAILY_REFRESH_HOUR=3
```

### 2. Запустите Docker Desktop

Дождитесь состояния `Engine running`, затем в корне репозитория выполните:

```powershell
docker compose --env-file .env.real up --build -d
```

В real-режиме mock-контейнер не запускается.

### 3. Проверьте запуск

```powershell
docker compose --env-file .env.real ps
docker compose --env-file .env.real logs --tail=100 devscope
```

Откройте `http://127.0.0.1:8080`.

### Применить изменения фронта и бэкенда

После `git pull` или редактирования исходников не нужно заново поднимать весь
compose-проект. Выполните одну команду:

```powershell
docker compose --env-file .env.real up -d --build devscope
```

Она пересобирает и заменяет только образ приложения `devscope`. Compose также
проверяет зависимость PostgreSQL: если база ещё не запущена, он запустит её и
дождётся состояния `healthy`; если уже работает — оставит как есть. Образ
PostgreSQL не пересобирается, volume с кэшем сохраняется, реальные MCP остаются
внешними. Docker использует кэш слоёв: `npm ci` повторяется только при изменении
`package.json`/lock-файла, а `dotnet restore` — только при изменении `.csproj`.

Не добавляйте `--no-deps` при первом запуске или после остановки PostgreSQL:
DevScope требует базу уже во время старта.

Посмотреть результат:

```powershell
docker compose --env-file .env.real logs --tail=100 devscope
```

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
youtrack_get_project(projectKey)
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

Наружу публикуется только интерфейс и только на loopback:

```text
127.0.0.1:8080 -> container:8080
```

MCP endpoints исходящие; порты для них и PostgreSQL на хосте не открываются. Не
меняйте `DEVSCOPE_BIND_ADDRESS` на `0.0.0.0`.

## Остановка

```powershell
docker compose --env-file .env.real down
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
