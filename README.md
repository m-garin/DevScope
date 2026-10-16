# DevScope

Локальный обзор работы сотрудника по данным YouTrack и GitLab. Источник часов —
worklog и его автор. Поле разработчика/исполнителя задачи не используется вместо
списаний.

## Запуск с реальными MCP

### 1. Проверьте локальные файлы

В каталоге `config` должны находиться:

- `mcp.json` — перенесённый вручную конфиг remote MCP в формате OpenCode;
- `teams.json` — сотрудники, их `youTrackLogin` и `gitLabUsername`.

Оба файла исключены из Git. DevScope ничего не копирует из OpenCode и не выводит
заголовки или токены в API.

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

Проверить соединение и увидеть реальные методы каждого MCP:

```text
http://127.0.0.1:8080/api/configuration/mcp/tools
```

У каждого сервера должно быть `"connected": true`. Поле `tools` содержит методы,
которые MCP действительно отдал приложению; секретные заголовки в ответ не входят.

Первое открытие сотрудника при пустом кэше получает данные через MCP и сохраняет
годовой снимок в PostgreSQL. В снимок входят YouTrack-задачи и worklog, а также
сведения о связанных GitLab MR, ветках, коммитах и обсуждениях. Дальше выбранные
периоды читаются из базы без новых MCP-запросов.

На экране «Настройки» выбираются:

- единая LLM-модель (по умолчанию `smart`);
- обновление кэша раз в час или раз в день (по умолчанию — раз в день в 03:00);
- немедленное обновление кэша кнопкой «Обновить сейчас».

Настройки хранятся в PostgreSQL и сохраняются после перезапуска. Фоновая джоба
обновляет только MCP-кэш и не вызывает LLM.

## Необходимый метод YouTrack MCP

Для построения отчёта по сотруднику DevScope вызывает read-only метод:

```text
youtrack_search_work_items(
  projectKey,
  authorLogin,
  dateFrom,
  dateTo,
  offset,
  limit)
```

Поддерживается пагинация и варианты массивов `workItemsPage`, `workItems`,
`worklogs` или `items`.

Если корпоративный YouTrack MCP не публикует метод чтения work items, приложение
вернёт HTTP 502 с точным списком доступных tools. Это ограничение самого MCP:
`youtrack_search_issues` и поле `$Разработчик` не позволяют достоверно определить,
кто и сколько времени списал. DevScope намеренно не подменяет эти данные.

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

## Формат `config/teams.json`

```json
{
  "teams": [
    {
      "id": "fintender-dev",
      "name": "Fintender · Разработка",
      "members": [
        {
          "id": "employee-1",
          "displayName": "Иван Петров",
          "youTrackLogin": "ivan.petrov",
          "email": "ivan.petrov@example.test",
          "gitLabUsername": "ipetrov",
          "roles": ["Backend"]
        }
      ]
    }
  ]
}
```

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
