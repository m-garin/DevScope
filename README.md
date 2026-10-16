# DevScope

Локальный обзор работы сотрудника по данным YouTrack и GitLab.

Источник часов — worklog и его автор. GitLab дополняет задачу ветками, коммитами,
MR и обсуждениями, но не используется для расчёта времени.

## Где лежит конфигурация MCP

DevScope читает только этот файл:

```text
config/mcp.json
```

Файл пользователь переносит и заполняет самостоятельно. DevScope не ищет и не
копирует конфигурацию OpenCode. Формат файла совместим с объектом `mcp` из
OpenCode; безопасный пример находится в `config/mcp.example.json`.

Требуемая структура:

```json
{
  "mcp": {
    "confluence": {
      "url": "https://ai-gateway.example/mcp/confluence",
      "type": "remote",
      "headers": {
        "X-MCP-Token": "REPLACE_LOCALLY",
        "X-Request-By": "personal",
        "X-MCP-Request-Principal": "user@example"
      }
    },
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
    "pyrus": {
      "url": "https://ai-gateway.example/mcp/pyrus",
      "type": "remote",
      "headers": {
        "X-MCP-Token": "REPLACE_LOCALLY",
        "X-Request-By": "personal",
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

`config/mcp.json` исключён из Git и монтируется в контейнер только для чтения.
Не сохраняйте реальные токены в `mcp.example.json`, `.env.mock` или `.env.real`.

## Запуск с mock MCP

Команда поднимает DevScope и отдельный mock MCP-контейнер:

```powershell
docker compose --env-file .env.mock --profile mock up --build -d
```

Открыть интерфейс:

```text
http://127.0.0.1:8080
```

Mock MCP доступен только внутри Docker-сети по адресу
`http://mock-mcp:8090/mcp`. Порт `8090` на рабочий компьютер не публикуется.

## Запуск с реальными MCP

Сначала самостоятельно положите заполненный файл в `config/mcp.json`, затем:

```powershell
docker compose --env-file .env.real up --build -d
```

Эта команда не поднимает mock MCP. Контейнер получает реальные настройки из
`/app/config/mcp.json`.

### Текущее ограничение real-режима

`McpActivityProvider` пока не реализован. Поэтому текущая версия при запуске с
`.env.real` завершается с явной ошибкой, а не подменяет реальные данные mock-данными.
Для рабочего real-режима необходимо реализовать MCP-клиент и получить read-only
метод выборки worklog по автору и периоду, например:

```text
youtrack_search_work_items(
  projectKey,
  authorLogin,
  dateFrom,
  dateTo,
  offset,
  limit)
```

Минимальный результат: `id`, `issueId`, `author`, `date`, `durationMinutes`,
`type`, `description`, `attributes` и пагинация.

## Сетевые порты

DevScope публикуется только на loopback-интерфейсе:

```text
127.0.0.1:8080 -> container:8080
```

Проверка:

```powershell
docker compose ps
Get-NetTCPConnection -LocalPort 8080 -State Listen |
  Select-Object LocalAddress, LocalPort, OwningProcess
```

В выводе должен быть `127.0.0.1`. Не устанавливайте
`DEVSCOPE_BIND_ADDRESS=0.0.0.0`, если сетевой доступ явно не согласован.

## Остановка

Для обоих режимов:

```powershell
docker compose --profile mock down
```

Именованный том с локальной SQLite-базой при этом сохраняется.

## Разработка без Docker

Frontend:

```powershell
cd src/DevScope.Web
npm install
npm run dev
```

Backend требует .NET 10 SDK.
